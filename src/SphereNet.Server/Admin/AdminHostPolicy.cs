using SphereNet.Core.Configuration;

namespace SphereNet.Server.Admin;

public static class AdminHostPolicy
{
    public static bool CanStartTelnet(SphereConfig config) =>
        !string.IsNullOrWhiteSpace(config.AdminPassword);
}
