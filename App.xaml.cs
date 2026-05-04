namespace Vamsync
    {
    public partial class App : Application
        {
        public App()
            {
            InitializeComponent();
            }

        protected override Window CreateWindow(IActivationState? activationState)
            {
            var win = new Window(new MainPage());
                win.TitleBar = new TitleBar
    {
        Title = "Vamsync Handy",

       
    };

    return win;
}




            }
        }
    
