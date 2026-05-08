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
            var win = new Window(new MainPage())
            {
               Width = 1200,
        Height = 900,
        X = 100,
        Y = 100

            };
                win.TitleBar = new TitleBar
    {
        Title = "Vamsync Handy",

       
    };

    return win;
}




            }
        }
    
