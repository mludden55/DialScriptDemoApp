namespace ConnectIVRTester
{
    /// <summary>Formats raw digit strings as (XXX) XXX-XXXX, including partial/in-progress input.</summary>
    public static class PhoneFormat
    {
        public static string ExtractDigits(string text) => new string(text.Where(char.IsDigit).ToArray());

        /// <summary>
        /// Extracts just the phone number's digits, ignoring anything after a " - "
        /// separator (used when a description is appended for display, e.g. after
        /// loading a script tied to a saved number). Without this, digits that happen
        /// to appear inside the description text (like "Line 2") could get folded into
        /// the phone number by a plain digit-only extraction.
        /// </summary>
        public static string ExtractPhoneDigits(string text)
        {
            var separatorIndex = text.IndexOf(" - ", StringComparison.Ordinal);
            var phonePart = separatorIndex >= 0 ? text.Substring(0, separatorIndex) : text;
            return ExtractDigits(phonePart);
        }

        /// <summary>Formats up to 10 digits, correctly handling partial input as the user types.</summary>
        public static string Format(string digits)
        {
            if (digits.Length > 10) digits = digits.Substring(0, 10);

            if (digits.Length == 0) return string.Empty;
            if (digits.Length < 4) return $"({digits}";
            if (digits.Length < 7) return $"({digits.Substring(0, 3)}){digits.Substring(3)}";
            return $"({digits.Substring(0, 3)}){digits.Substring(3, 3)}-{digits.Substring(6)}";
        }
    }
}
