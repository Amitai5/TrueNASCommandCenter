namespace TrueNasCommandCenter.Integrations.TrueNas;

/// <summary>Provides read-only access to the TrueNAS application catalog.</summary>
public interface ITrueNasCatalogClient
{
    /// <summary>Returns every catalog app grouped by train and catalog name.</summary>
    /// <param name="forceRefresh">Whether TrueNAS should bypass its cached catalog response.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The catalog apps grouped by train and app name.</returns>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, TrueNasCatalogAppDto>>> QueryCatalogAppsAsync(bool forceRefresh, CancellationToken cancellationToken = default);
    /// <summary>Returns the detailed catalog record for one train and app name.</summary>
    /// <param name="appName">The catalog app name.</param>
    /// <param name="train">The catalog train.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The detailed catalog app record.</returns>
    Task<TrueNasCatalogAppDto> GetCatalogAppDetailsAsync(string appName, string train, CancellationToken cancellationToken = default);
    /// <summary>Returns apps that TrueNAS considers similar within the same train.</summary>
    /// <param name="appName">The catalog app name.</param>
    /// <param name="train">The catalog train.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The similar catalog apps.</returns>
    Task<IReadOnlyList<TrueNasCatalogAppDto>> QuerySimilarCatalogAppsAsync(string appName, string train, CancellationToken cancellationToken = default);
}
