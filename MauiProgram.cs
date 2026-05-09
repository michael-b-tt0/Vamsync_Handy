using handyapiv3;
using handyapiv3.Abstractions;
using handyapiv3.Services;
using Microsoft.Extensions.Logging;
using Vamsync.Services;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Vamsync
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {

            var userDataFolder = Path.Combine(
            FileSystem.AppDataDirectory,
            "WebView2"
        );

        Directory.CreateDirectory(userDataFolder);

        Environment.SetEnvironmentVariable(
            "WEBVIEW2_USER_DATA_FOLDER",
            userDataFolder
        );
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();
            builder.Services.AddFluentUIComponents();


#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            builder.Services.AddSingleton(new HttpClient());
            builder.Services.AddSingleton(new HandyApiV3ClientOptions
            {
                ConnectionKey = null,
                ApplicationApiKey = "wKTdv0fJNfBRdkf4-x5gUvtSuNPWzv-s",
            });
            builder.Services.AddSingleton<HandyApiV3Client>();
            builder.Services.AddSingleton<IHandyService, HandyService>();
            builder.Services.AddSingleton<AppState>();
            builder.Services.AddSingleton<MotionCsvLogger>();
            builder.Services.AddSingleton<IUdpMotionParser, BinaryMotionPacketParser>();
            builder.Services.AddSingleton<IUdpMotionParser, TCodeMotionParser>();
            builder.Services.AddSingleton<UdpMotionListener>();
            builder.Services.AddSingleton<HandyBridgeService>();

            return builder.Build();
        }
    }
}
