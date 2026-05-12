namespace Vamsync
    {
    public partial class MainPage : ContentPage
        {
        private bool _startupNetworkCheckCompleted;

        public MainPage()
            {
            InitializeComponent();
            }

        protected override async void OnAppearing()
            {
            base.OnAppearing();

            if (_startupNetworkCheckCompleted)
                {
                return;
                }

            _startupNetworkCheckCompleted = true;

            var networkAccess = Connectivity.Current.NetworkAccess;
            if (networkAccess == NetworkAccess.Internet)
                {
                return;
                }

            await DisplayAlert(
                "Internet connection required",
                $"VaMSync needs an active internet connection to work. Current network state: {DescribeNetworkAccess(networkAccess)}.",
                "OK");
            }

        private static string DescribeNetworkAccess(NetworkAccess networkAccess) =>
            networkAccess switch
                {
                NetworkAccess.None => "No network connection",
                NetworkAccess.Local => "Local network only",
                NetworkAccess.ConstrainedInternet => "Limited internet access",
                NetworkAccess.Unknown => "Unknown network status",
                _ => networkAccess.ToString(),
                };
        }
    }
