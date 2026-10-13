using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectIVRTester
{
    public class PhoneBookEntry
    {
        public string Number { get; set; } = string.Empty; // raw 10 digits
        public string Description { get; set; } = string.Empty;

        [JsonIgnore]
        public string FormattedNumber => PhoneFormat.Format(Number);
        [JsonIgnore]
        public string DisplayText => string.IsNullOrEmpty(Description) ? FormattedNumber : $"{FormattedNumber} \u2014 {Description}";
    }

    /// <summary>
    /// The saved list of phone numbers (with descriptions) shown in the "Phone Number
    /// Builder" and used to power the autocomplete lookup on the Call Target field.
    /// Stored locally so it persists across sessions.
    /// </summary>
    public class PhoneBook
    {
        public List<PhoneBookEntry> Entries { get; set; } = new();

        public static string FilePath =>
            Path.Combine(AppPaths.EnvironmentDir(EnvironmentContext.Current), "phonebook.json");

        public static PhoneBook Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var loaded = JsonSerializer.Deserialize<PhoneBook>(json, options);
                    if (loaded != null) return loaded;
                }
            }
            catch
            {
                // Corrupt file - start fresh rather than crash on startup.
            }
            return new PhoneBook();
        }

        public void Save()
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }

        /// <summary>Adds a number (with description) if not already present, keeping the list numerically sorted.</summary>
        public bool AddNumber(string digits, string description)
        {
            if (Entries.Any(e => e.Number == digits)) return false;
            Entries.Add(new PhoneBookEntry { Number = digits, Description = description });
            SortNumerically();
            return true;
        }

        public void RemoveNumber(string digits) => Entries.RemoveAll(e => e.Number == digits);

        public void SortNumerically() => Entries.Sort((a, b) => string.CompareOrdinal(a.Number, b.Number));

        /// <summary>Entries whose number contains the given digit sequence, sorted numerically.</summary>
        public List<PhoneBookEntry> Search(string digitQuery)
        {
            if (string.IsNullOrEmpty(digitQuery)) return new List<PhoneBookEntry>(Entries);
            return Entries.Where(e => e.Number.Contains(digitQuery))
                          .OrderBy(e => e.Number, StringComparer.Ordinal)
                          .ToList();
        }

        /// <summary>
        /// Entries matching either the raw phone number (if the query has digits in it)
        /// or the description text (case-insensitive substring match), so typing a name
        /// like "MyPhone" in Call Target finds it by description, while typing digits
        /// still searches by number as before.
        /// </summary>
        public List<PhoneBookEntry> SearchByNumberOrDescription(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<PhoneBookEntry>(Entries);

            var digits = PhoneFormat.ExtractDigits(query);
            return Entries.Where(e =>
                    (digits.Length > 0 && e.Number.Contains(digits)) ||
                    e.Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(e => e.Number, StringComparer.Ordinal)
                .ToList();
        }
    }
}
