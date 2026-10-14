using Dexter;

var settings = Settings.Load();

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<IMailbox, GmailMailbox>();
builder.Services.AddSingleton<IAgentAdapter, ClaudeAgent>();
builder.Services.AddSingleton<StatusReporter>();
builder.Services.AddSingleton<Dispatcher>();
builder.Services.AddHostedService<RelayWorker>();

builder.Build().Run();
