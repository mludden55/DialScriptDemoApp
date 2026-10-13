using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ConnectIVRTester
{
    public enum StepType
    {
        Numeric,
        Voice,
        HangUp
    }

    /// <summary>
    /// One entry in the Step Builder list. Raises PropertyChanged so the
    /// UI (ComboBox / TextBox bindings) stays in sync with edits.
    /// </summary>
    public class Step : INotifyPropertyChanged
    {
        private StepType _type = StepType.Numeric;
        private string _value = string.Empty;
        private int _pauseSecondsBefore = 3;

        public int Number { get; set; } // 1-based display order, set by MainWindow whenever the list changes

        public StepType Type
        {
            get => _type;
            set
            {
                if (_type != value)
                {
                    _type = value;
                    // Changing type invalidates whatever was typed under the old rules.
                    Value = string.Empty;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PlaceholderText));
                    OnPropertyChanged(nameof(IsValueEditable));
                }
            }
        }

        public string Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _value = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>How long to wait before this step runs. Tune this per step based on
        /// how long the real IVR's prompt actually takes, since Twilio has no way to
        /// automatically detect when the far end stops talking.</summary>
        public int PauseSecondsBefore
        {
            get => _pauseSecondsBefore;
            set
            {
                if (_pauseSecondsBefore != value)
                {
                    _pauseSecondsBefore = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsValueEditable => Type != StepType.HangUp;

        public string PlaceholderText => Type switch
        {
            StepType.Numeric => "Digits, #, *, w(pause), e.g. 323445",
            StepType.Voice => "Text to speak",
            _ => "(ends the call - no value needed)"
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
