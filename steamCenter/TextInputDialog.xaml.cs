using System.Windows;

namespace steamCenter
{
    public partial class TextInputDialog : Window
    {
        public string InputText { get; private set; } = "";

        public TextInputDialog(string title, string initialText = "")
        {
            InitializeComponent();
            Title = title;
            MessageText.Text = title;
            InputTextBox.Text = initialText;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            InputText = InputTextBox.Text;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}