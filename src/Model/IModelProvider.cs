using AIWorldPawnshop.Models;

namespace AIWorldPawnshop.Model;

public interface IModelProvider
{
    Task<ConversationResponse?> CompleteAsync(
        ConversationRequest request,
        CancellationToken cancellationToken);
}
