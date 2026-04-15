// Ticketing/Contracts/ITicketingProvider.cs
//
// Core contract. Every ticketing platform implements this.
// BLT code ONLY talks to this interface — never to a concrete provider.

namespace BLT.Agent.Ticketing.Contracts;

public interface ITicketingProvider
{
    // Human-readable name for logging (e.g. "JIRA Data Center", "Azure DevOps")
    string ProviderName { get; }

    // Create a ticket. Returns a normalised result regardless of platform.
    Task<TicketResult> CreateTicketAsync(
        TicketCreateRequest request,
        CancellationToken ct = default);

    // Optional: get ticket status after creation
    Task<TicketStatus?> GetTicketStatusAsync(
        string ticketKey,
        CancellationToken ct = default);

    // Health check — can we reach the platform?
    Task<bool> IsHealthyAsync(CancellationToken ct = default);
}
