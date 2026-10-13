using System.IO;
using System.Windows;

namespace ConnectIVRTester
{
    public partial class SaveNameDialog : Window
    {
        private readonly string _extension;
        private readonly string _originalDefaultName;
        private readonly string _originalDefaultFolder;

        /// <summary>Full path the user chose, set only when the dialog closes with Save.</summary>
        public string? ResultPath { get; private set; }

        /// <summary>
        /// True if the user actually edited the name or folder from what was pre-filled.
        /// Checking this directly (rather than reconstructing and comparing file paths
        /// afterward) avoids false positives from any path-normalization subtlety - if
        /// the visible text never changed, nothing changed, full stop.
        /// </summary>
        public bool NameOrFolderChanged =>
            NameBox.Text != _originalDefaultName || FolderBox.Text != _originalDefaultFolder;

        public SaveNameDialog(string title, string promptText, string defaultName, string defaultFolder, string extension)
        {
            InitializeComponent();
            Title = title;
            PromptText.Text = promptText;
            _extension = extension.StartsWith(".") ? extension : "." + extension;
            _originalDefaultName = defaultName;
            _originalDefaultFolder = defaultFolder;

            NameBox.Text = defaultName;
            FolderBox.Text = defaultFolder;
            UpdatePreview();

            NameBox.TextChanged += (_, _) => UpdatePreview();
            Loaded += (_, _) =>
            {
                NameBox.Focus();
                // Caret at the end with nothing selected, so if the user just starts
                // typing it appends to the default name instead of replacing it.
                NameBox.CaretIndex = NameBox.Text.Length;
            };
        }

        private void UpdatePreview()
        {
            var safeName = SanitizeFileName(NameBox.Text.Trim());
            FullPathPreview.Text = string.IsNullOrEmpty(safeName)
                ? "Enter a name above."
                : $"Will save as: {Path.Combine(FolderBox.Text, safeName + _extension)}";
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose a folder (local or network drive)",
                UseDescriptionForTitle = true,
                // Rooting at "This PC" instead of the default virtual Desktop node avoids
                // a known FolderBrowserDialog quirk where a pre-selected absolute path
                // (e.g. an already-used Documents subfolder) can resolve to the wrong
                // shell location under the Desktop root.
                RootFolder = Environment.SpecialFolder.MyComputer,
                SelectedPath = Directory.Exists(FolderBox.Text) ? FolderBox.Text : ""
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                FolderBox.Text = Path.GetFullPath(dialog.SelectedPath);
                UpdatePreview();
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var safeName = SanitizeFileName(NameBox.Text.Trim());
            if (string.IsNullOrEmpty(safeName))
            {
                System.Windows.MessageBox.Show(this, "Enter a name before saving.", "Name required",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!Directory.Exists(FolderBox.Text))
            {
                System.Windows.MessageBox.Show(this, "Choose a valid save location first.", "Location required",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultPath = Path.Combine(FolderBox.Text, safeName + _extension);
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
