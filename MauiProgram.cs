using handyapiv3;
using handyapiv3.Abstractions;
using handyapiv3.Services;
using Microsoft.Extensions.Logging;
using Vamsync.Services;

namespace Vamsync
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            builder.Services.AddSingleton(new HttpClient());
            builder.Services.AddSingleton(new HandyApiV3ClientOptions
            {
                ConnectionKey = null,
            });
            builder.Services.AddSingleton<HandyApiV3Client>();
            builder.Services.AddSingleton<IHandyService, HandyService>();
            builder.Services.AddSingleton<AppState>();
            builder.Services.AddSingleton<UdpMotionListener>();
            builder.Services.AddSingleton<HandyBridgeService>();

            return builder.Build();
        }
    }
}
