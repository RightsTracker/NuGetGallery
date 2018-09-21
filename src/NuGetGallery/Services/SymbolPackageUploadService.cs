﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGetGallery.Packaging;

namespace NuGetGallery
{
    public class SymbolPackageUploadService : ISymbolPackageUploadService
    {
        private readonly IEntitiesContext _entitiesContext;
        private readonly IValidationService _validationService;
        private readonly ISymbolPackageService _symbolPackageService;
        private readonly ISymbolPackageFileService _symbolPackageFileService;
        private readonly IContentObjectService _contentObjectService;
        private readonly IPackageService _packageService;
        private readonly ITelemetryService _telemetryService;

        public SymbolPackageUploadService(
            ISymbolPackageService symbolPackageService,
            ISymbolPackageFileService symbolPackageFileService,
            IEntitiesContext entitiesContext,
            IValidationService validationService,
            IPackageService packageService,
            ITelemetryService telemetryService,
            IContentObjectService contentObjectService)
        {
            _symbolPackageService = symbolPackageService ?? throw new ArgumentNullException(nameof(symbolPackageService));
            _symbolPackageFileService = symbolPackageFileService ?? throw new ArgumentNullException(nameof(symbolPackageFileService));
            _entitiesContext = entitiesContext ?? throw new ArgumentNullException(nameof(entitiesContext));
            _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
            _packageService = packageService ?? throw new ArgumentNullException(nameof(packageService));
            _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
            _contentObjectService = contentObjectService ?? throw new ArgumentNullException(nameof(contentObjectService));
        }

        public async Task<PackageUploadOperationResult> ValidateUploadedSymbolPackage(Stream symbolPackageStream, User currentUser)
        {
            Package package = null;

            // Check if symbol package upload is allowed for this user.
            if (!_contentObjectService.SymbolsConfiguration.IsSymbolsUploadEnabledForUser(currentUser))
            {
                return new PackageUploadOperationResult(PackageUploadResult.Unauthorized,
                    Strings.SymbolsPackage_UploadNotAllowed,
                    success: false);
            }

            try
            {
                if (symbolPackageStream.FoundEntryInFuture(out ZipArchiveEntry entryInTheFuture))
                {
                    return new PackageUploadOperationResult(PackageUploadResult.BadRequest, string.Format(
                        CultureInfo.CurrentCulture,
                        Strings.PackageEntryFromTheFuture,
                        entryInTheFuture.Name),
                        success: false);
                }

                using (var packageToPush = new PackageArchiveReader(symbolPackageStream, leaveStreamOpen: true))
                {
                    var nuspec = packageToPush.GetNuspecReader();
                    var id = nuspec.GetId();
                    var version = nuspec.GetVersion();
                    var normalizedVersion = version.ToNormalizedStringSafe();

                    // Ensure the corresponding package exists before pushing a snupkg.
                    package = _packageService.FindPackageByIdAndVersionStrict(id, version.ToStringSafe());
                    if (package == null || package.PackageStatusKey == PackageStatus.Deleted)
                    {
                        return new PackageUploadOperationResult(PackageUploadResult.NotFound, string.Format(
                            CultureInfo.CurrentCulture,
                            Strings.SymbolsPackage_PackageIdAndVersionNotFound,
                            id,
                            normalizedVersion),
                            success: false);
                    }

                    // Do not allow to upload a snupkg to a package which has symbols package pending validations.
                    if (package.SymbolPackages.Any(sp => sp.StatusKey == PackageStatus.Validating))
                    {
                        return new PackageUploadOperationResult(PackageUploadResult.Conflict,
                            Strings.SymbolsPackage_ConflictValidating,
                            success: false);
                    }

                    try
                    {
                        await _symbolPackageService.EnsureValidAsync(packageToPush);
                    }
                    catch (Exception ex)
                    {
                        ex.Log();

                        var message = Strings.SymbolsPackage_FailedToReadPackage;
                        if (ex is InvalidPackageException || ex is InvalidDataException || ex is EntityException)
                        {
                            message = ex.Message;
                        }

                        _telemetryService.TrackSymbolPackageFailedGalleryValidationEvent(id, normalizedVersion);
                        return new PackageUploadOperationResult(PackageUploadResult.BadRequest,
                            message,
                            success: false);
                    }
                }

                return new PackageUploadOperationResult(package: package, success: true);
            }
            catch (Exception ex) when (ex is InvalidPackageException
                || ex is InvalidDataException
                || ex is EntityException
                || ex is FrameworkException)
            {
                return new PackageUploadOperationResult(
                    PackageUploadResult.BadRequest,
                    string.Format(CultureInfo.CurrentCulture, Strings.UploadPackage_InvalidPackage, ex.Message),
                    success: false);
            }
        }

        /// <summary>
        /// This method creates the symbol db entities and invokes the validations for the uploaded snupkg. 
        /// It will send the message for validation and upload the snupkg to the "validations"/"symbols-packages" container
        /// based on the result. It will then update the references in the database for persistence with appropriate status.
        /// </summary>
        /// <param name="package">The package for which symbols package is to be uplloaded</param>
        /// <param name="packageStreamMetadata">The package stream metadata for the uploaded symbols package file.</param>
        /// <param name="symbolPackageFile">The symbol package file stream.</param>
        /// <returns>The <see cref="PackageUploadOperationResult"/> for the create and upload symbol package flow.</returns>
        public async Task<PackageUploadOperationResult> CreateAndUploadSymbolsPackage(Package package, Stream symbolPackageStream)
        {
            var packageStreamMetadata = new PackageStreamMetadata
            {
                HashAlgorithm = CoreConstants.Sha512HashAlgorithmId,
                Hash = CryptographyService.GenerateHash(
                    symbolPackageStream.AsSeekableStream(),
                    CoreConstants.Sha512HashAlgorithmId),
                Size = symbolPackageStream.Length
            };

            Stream symbolPackageFile = symbolPackageStream.AsSeekableStream();

            var symbolPackage = _symbolPackageService.CreateSymbolPackage(package, packageStreamMetadata);

            await _validationService.StartValidationAsync(symbolPackage);

            if (symbolPackage.StatusKey != PackageStatus.Available
                && symbolPackage.StatusKey != PackageStatus.Validating)
            {
                throw new InvalidOperationException(
                    $"The symbol package to commit must have either the {PackageStatus.Available} or {PackageStatus.Validating} package status.");
            }

            try
            {
                if (symbolPackage.StatusKey == PackageStatus.Validating)
                {
                    await _symbolPackageFileService.SaveValidationPackageFileAsync(symbolPackage.Package, symbolPackageFile);
                }
                else if (symbolPackage.StatusKey == PackageStatus.Available)
                {
                    if (!symbolPackage.Published.HasValue)
                    {
                        symbolPackage.Published = DateTime.UtcNow;
                    }

                    // Mark any other associated available symbol packages for deletion.
                    var availableSymbolPackages = package
                        .SymbolPackages
                        .Where(sp => sp.StatusKey == PackageStatus.Available
                            && sp != symbolPackage);

                    var overwrite = false;
                    if (availableSymbolPackages.Any())
                    {
                        // Mark the currently available packages for deletion, and replace the file in the container.
                        foreach (var availableSymbolPackage in availableSymbolPackages)
                        {
                            availableSymbolPackage.StatusKey = PackageStatus.Deleted;
                        }

                        overwrite = true;
                    }

                    // Caveat: This doesn't really affect our prod flow since the package is validating, however, when the async validation
                    // is disabled there is a chance that there could be concurrency issues when pushing multiple symbols simultaneously. 
                    // This could result in an inconsistent data or multiple symbol entities marked as available. This could be sovled using etag
                    // for saving files, however since it doesn't really affect nuget.org which happen have async validations flow I will leave it as is.
                    await _symbolPackageFileService.SavePackageFileAsync(symbolPackage.Package, symbolPackageFile, overwrite);
                }

                try
                {
                    // commit all changes to database as an atomic transaction
                    await _entitiesContext.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    ex.Log();

                    // If saving to the DB fails for any reason we need to delete the package we just saved.
                    if (symbolPackage.StatusKey == PackageStatus.Validating)
                    {
                        await _symbolPackageFileService.DeleteValidationPackageFileAsync(
                            package.PackageRegistration.Id,
                            package.Version);
                    }
                    else if (symbolPackage.StatusKey == PackageStatus.Available)
                    {
                        await _symbolPackageFileService.DeletePackageFileAsync(
                            package.PackageRegistration.Id,
                            package.Version);
                    }

                    throw ex;
                }
            }
            catch (FileAlreadyExistsException ex)
            {
                ex.Log();
                return new PackageUploadOperationResult(
                    PackageUploadResult.Conflict,
                    Strings.SymbolsPackage_ConflictValidating, 
                    success: false);
            }

            _telemetryService.TrackSymbolPackagePushEvent(package.Id, package.NormalizedVersion);

            return new PackageUploadOperationResult(PackageUploadResult.Created, success: true);
        }
    }
}
