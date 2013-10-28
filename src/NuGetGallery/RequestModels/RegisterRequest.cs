﻿using System;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using NuGetGallery.Infrastructure;

namespace NuGetGallery
{
    public class RegisterRequest
    {
        // Note: regexes must be tested to work in javascript
        // We do NOT follow strictly the RFCs at this time, and we choose not to support many obscure email address variants. 
        // Specifically the following are not supported by-design:
        //  * Addresses containing () or []
        //  * Second parts with no dots (i.e. foo@localhost or foo@com)
        //  * Addresses with quoted (" or ') first parts
        //  * Addresses with IP Address second parts (foo@[127.0.0.1])
        internal const string FirstPart = @"[-A-Za-z0-9!#$%&'*+\/=?^_`{|}~\.]+";
        internal const string SecondPart = @"[A-Za-z0-9]+[\w\.-]*[A-Za-z0-9]*\.[A-Za-z0-9][A-Za-z\.]*[A-Za-z]";
        internal const string EmailValidationRegex ="^" + FirstPart + "@" + SecondPart + "$";

        internal const string EmailValidationErrorMessage = "This doesn't appear to be a valid email address.";

        internal const string UsernameValidationRegex =
            @"[A-Za-z0-9][A-Za-z0-9_\.-]+[A-Za-z0-9]";

        /// <summary>
        /// Regex that matches INVALID username characters, to make it easy to strip those characters out.
        /// </summary>
        internal static readonly Regex UsernameNormalizationRegex =
            new Regex(@"[^A-Za-z0-9_\.-]");

        internal const string UsernameValidationErrorMessage =
            "User names must start and end with a letter or number, and may only contain letters, numbers, underscores, periods, and hyphens in between.";

        [Required]
        [StringLength(255)]
        [Display(Name = "Email")]
        //[DataType(DataType.EmailAddress)] - does not work with client side validation
        [RegularExpression(EmailValidationRegex, ErrorMessage = EmailValidationErrorMessage)]
        [Hint(
            "Your email will not be public unless you choose to disclose it. " +
            "It is required to verify your registration and for password retrieval, important notifications, etc. ")]
        [Subtext("We use <a href=\"http://www.gravatar.com\" target=\"_blank\">Gravatar</a> to get your profile picture", AllowHtml = true)]
        public string EmailAddress { get; set; }

        [Required]
        [StringLength(64)]
        [RegularExpression(UsernameValidationRegex, ErrorMessage = UsernameValidationErrorMessage)]
        [Hint("Choose something unique so others will know which contributions are yours.")]
        public string Username { get; set; }

        public virtual string Password { get; set; }

        public bool ShowPassword { get; set; }

        /// <summary>
        /// Takes in a potential username string and returns a version with invalid characters stripped out.
        /// </summary>
        /// <param name="candidateUserName">The user name to strip</param>
        /// <returns></returns>
        public static string NormalizeUserName(string candidateUserName)
        {
            // Remove characters that aren't allowed as prefixes/suffixes
            if (!String.IsNullOrEmpty(candidateUserName) && !Char.IsLetterOrDigit(candidateUserName[0]))
            {
                candidateUserName = candidateUserName.Substring(1);
            }
            if (!String.IsNullOrEmpty(candidateUserName) && !Char.IsLetterOrDigit(candidateUserName[candidateUserName.Length - 1]))
            {
                candidateUserName = candidateUserName.Substring(0, candidateUserName.Length - 1);
            }

            // Strip inner characters that are invalid
            return UsernameNormalizationRegex.Replace(candidateUserName, "");
        }
    }

    public class RegisterLocalUserRequest : RegisterRequest
    {
        [Required]
        [DataType(DataType.Password)]
        [StringLength(64, MinimumLength = 7)]
        [Hint("Passwords must be at least 7 characters long.")]
        public override string Password { get; set; }
    }
}