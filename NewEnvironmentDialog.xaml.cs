using System.IO;
using System.Linq;
using System.Windows;

namespace ConnectIVRTester
{
    public partial class NewEnvironmentDialog : Window
    {
        private readonly List<string> _existing;

        /// <summary>The new environment name, set only when the dialog closes via Add.</summary>
        public string? ResultName { get; private set; }

        public NewEnvironmentDialog(List<string> existingEnvironments)
        {
            InitializeComponent();
            _existing = existingEnvironments;
            Loaded += (_, _) => NameBox.Focus();
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text.Trim();

            if (string.IsNullOrEmpty(name))
            {
                System.Windows.MessageBox.Show(this, "Enter a name for the environment.", "Name required",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_existing.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            {
                System.Windows.MessageBox.Show(this, $"An environment named \"{name}\" already exists.",
                    "Already exists", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // The name becomes a folder name on disk, so it needs to be filesystem-safe.
            var invalid = Path.GetInvalidFileNameChars();
            if (name.Any(c => invalid.Contains(c)))
            {
                System.Windows.MessageBox.Show(this,
                    "Environment names can't contain characters that aren't valid in a folder name.",
                    "Invalid characters", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultName = name;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
