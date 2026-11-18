using Microsoft.AspNetCore.SignalR;

namespace MudMaps.Server.Maps;

/// <summary>Push-only channel: the server sends "mapUpdated" with the new version. Clients cannot call anything.</summary>
public sealed class MapHub : Hub;
