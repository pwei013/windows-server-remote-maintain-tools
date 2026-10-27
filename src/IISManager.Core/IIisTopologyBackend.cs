using IISManager.Contracts;

namespace IISManager.Core;

public interface IIisTopologyBackend
{
    Task<SiteTopologySnapshot> GetSiteTopologyAsync(CancellationToken cancellationToken = default);
}
