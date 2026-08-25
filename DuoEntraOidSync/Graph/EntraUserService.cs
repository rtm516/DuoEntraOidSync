using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace DuoEntraOidSync.Graph;

/// <summary>
/// Builds an in-memory index that maps a UPN (or mail) to the Entra object id (oid).
/// </summary>
public sealed class EntraUserService
{
    // Microsoft Graph's maximum $top page size for users is 999; 1000 is rejected.
    private const int GraphUserPageSize = 999;

    private readonly GraphServiceClient _graph;
    private readonly ILogger<EntraUserService> _logger;

    public EntraUserService(GraphServiceClient graph, ILogger<EntraUserService> logger)
    {
        _graph = graph;
        _logger = logger;
    }

    /// <summary>
    /// Pulls the full user directory (id, userPrincipalName, mail) and returns a
    /// case-insensitive lookup keyed by both UPN and mail, valued by oid.
    ///
    /// Duo's alias1 holds a UPN, but for guest/cross-tenant users the value stored
    /// may be the member's real email rather than the synthetic Entra UPN
    /// (foo_contoso.com#EXT#@tenant.onmicrosoft.com). Indexing by mail as well as UPN
    /// lets those users still resolve. UPN always wins a key collision.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> BuildOidIndexAsync(CancellationToken cancellationToken)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var userCount = 0;

        var page = await _graph.Users.GetAsync(request =>
        {
            request.QueryParameters.Select = ["id", "userPrincipalName", "mail"];
            request.QueryParameters.Top = GraphUserPageSize;
        }, cancellationToken);

        if (page is null)
        {
            return index;
        }

        var iterator = PageIterator<User, UserCollectionResponse>.CreatePageIterator(
            _graph,
            page,
            user =>
            {
                userCount++;
                var oid = user.Id;
                if (string.IsNullOrWhiteSpace(oid))
                {
                    return true;
                }

                // UPN takes precedence: unconditional set overwrites any prior mail entry.
                if (!string.IsNullOrWhiteSpace(user.UserPrincipalName))
                {
                    index[user.UserPrincipalName] = oid;
                }

                // Mail only fills a slot a UPN hasn't already claimed.
                if (!string.IsNullOrWhiteSpace(user.Mail))
                {
                    index.TryAdd(user.Mail, oid);
                }

                return true;
            });

        await iterator.IterateAsync(cancellationToken);

        _logger.LogInformation("Pulled {UserCount} Entra users ({KeyCount} lookup keys).", userCount, index.Count);
        return index;
    }
}
