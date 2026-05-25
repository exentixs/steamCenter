using System.Windows;
using System.Windows.Threading;

namespace steamCenter
{
    public partial class NotificationWindow : Window
    {
        private DispatcherTimer _timer;

        public NotificationWindow(string message, Window owner)
        {
            InitializeComponent();
            MessageText.Text = message;

            double x = owner.Left + (owner.Width / 2) - 150;
            double y = owner.Top + owner.Height - 80;
            Left = x;
            Top = y;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (s, e) => { _timer.Stop(); Close(); };
            _timer.Start();
        }
    }
}