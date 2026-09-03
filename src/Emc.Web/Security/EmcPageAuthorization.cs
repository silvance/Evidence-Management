using Emc.Application.Authorization;

namespace Emc.Web.Security;

/// <summary>
/// Page-level authorization helper.
///
/// Every accountability page calls this explicitly. There is no default-allow and no
/// administrator bypass: the decision comes from <see cref="IEvidenceAuthorizationService"/>,
/// which checks the database-resolved roles and, for evidence-room actions, an active written
/// custodian appointment under AR 195-5 para 1-4g(1) (IAM-002, IAM-005, IAM-009).
/// </summary>
public interface IEmcPageAuthorization
{
    Task<AuthorizationDecision> CheckAsync(string permission, int? evidenceRoomId = null);
}

public sealed class EmcPageAuthorization : IEmcPageAuthorization
{
    private readonly IEvidenceAuthorizationService _authorization;

    public EmcPageAuthorization(IEvidenceAuthorizationService authorization)
        => _authorization = authorization;

    public Task<AuthorizationDecision> CheckAsync(string permission, int? evidenceRoomId = null)
        => _authorization.AuthorizeAsync(permission, evidenceRoomId);
}
