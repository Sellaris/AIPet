using System.Windows;

namespace VPet_AIGF
{
    public partial class InputDialog : Window
    {
        public string InputText { get; private set; } = "";

        public InputDialog(string prompt, string defaultText = "")
        {
            InitializeComponent();
            tbPrompt.Text = prompt;
            txtInput.Text = defaultText;
            txtInput.SelectAll();
            txtInput.Focus();
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            InputText = txtInput.Text;
            DialogResult = true;
            Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}