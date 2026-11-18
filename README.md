# Mud Maps

Maps of the Mud ecosystem's code, lit by the last 30 days of pull requests. Each repository's
Graphify code graph becomes areas (component folders) and rooms
(code communities), and pull requests are laid over them file by file.

Four designs share one data model:

| Segment | Design |
|---|---|
| `colony` | An ant farm; ants walk where this month's pull requests went |
| `city` | Every repo as one city, lit by pull requests |
| `islands` | A printed sea chart; flags mark pull requests |
| `space` | A star per repo with its code in orbit; moons are pull requests |

## Projects

- `src/MudMaps.Server`: ASP.NET Core service. Builds the map from the graphs, polls GitHub for pull
  request activity, pushes updates over SignalR (`hubs/map`), serves `api/map`, `api/health`, a chooser page
  and the four published apps.
- `src/MudMaps.Shared`: Razor class library with the data service, layout helpers and shared panels.
- `src/MudMaps.Colony`, `City`, `Islands`, `Stars`: Blazor WebAssembly apps built with
  [MudBlazor](https://mudblazor.com). Each falls back to a bundled snapshot when no service is present.
- `tests/MudMaps.Server.Tests`: xUnit tests for the service.

## Build and run

Requires the .NET 10 SDK.

```bash
dotnet build MudMaps.slnx
dotnet test tests/MudMaps.Server.Tests

# Publish the apps and a self-contained server to ./artifacts, mounted under /mudmap
MUDMAPS_PATH_BASE=/mudmap tools/publish.sh
```

The service reads `graphify-out/graph.json` from `<Maps:ReposRoot>/<owner>/<repo>`. Configuration lives in
`appsettings.json` under `Maps` and can be overridden with environment variables (`Maps__PathBase`,
`Maps__ReposRoot`, `Maps__Sites__<segment>`, `Maps__FrameAncestors__0`, ...). A GitHub token with public
read-only access enables live pull request data; see `deploy/mudmaps-server.service` for a hardened systemd unit.

## License

[MIT](LICENSE)
