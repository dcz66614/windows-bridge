using Bridge.RelayService;

var app = RelayApp.CreateApp(args);
await RelayApp.InitializeAsync(app);
app.Run();

// Public for WebApplicationFactory in tests.
public partial class Program
{
}
