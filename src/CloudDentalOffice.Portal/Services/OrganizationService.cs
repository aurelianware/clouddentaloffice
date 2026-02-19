namespace CloudDentalOffice.Portal.Services;

using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using Microsoft.EntityFrameworkCore;

public class OrganizationService : IOrganizationService
{
    private readonly CloudDentalDbContext _dbContext;
    private readonly ILogger<OrganizationService> _logger;

    public OrganizationService(CloudDentalDbContext dbContext, ILogger<OrganizationService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Organization?> GetOrganizationByIdAsync(int organizationId)
    {
        return await _dbContext.Organizations
            .Include(o => o.Users)
            .FirstOrDefaultAsync(o => o.Id == organizationId);
    }

    public async Task<Organization?> GetOrganizationByTenantIdAsync(string tenantId)
    {
        return await _dbContext.Organizations
            .Include(o => o.Users)
            .FirstOrDefaultAsync(o => o.TenantId == tenantId);
    }

    public async Task<Organization?> GetOrganizationByAzureAdTenantIdAsync(string azureAdTenantId)
    {
        return await _dbContext.Organizations
            .Include(o => o.Users)
            .FirstOrDefaultAsync(o => o.AzureAdTenantId == azureAdTenantId);
    }

    public async Task<Organization> CreateOrganizationAsync(Organization organization)
    {
        // Generate unique TenantId if not provided
        if (string.IsNullOrEmpty(organization.TenantId))
        {
            organization.TenantId = Guid.NewGuid().ToString();
        }

        // Set default values
        organization.CreatedAt = DateTime.UtcNow;
        organization.IsActive = true;

        // Set trial expiration if on trial plan
        if (organization.Plan == "trial" && organization.TrialExpiresAt == null)
        {
            organization.TrialExpiresAt = DateTime.UtcNow.AddDays(14);
        }

        _dbContext.Organizations.Add(organization);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created organization: {Name} (ID: {Id}, TenantId: {TenantId})", 
            organization.Name, organization.Id, organization.TenantId);

        return organization;
    }

    public async Task<Organization> UpdateOrganizationAsync(Organization organization)
    {
        var existing = await _dbContext.Organizations.FindAsync(organization.Id);
        if (existing == null)
        {
            throw new InvalidOperationException($"Organization with ID {organization.Id} not found");
        }

        // Update properties
        existing.Name = organization.Name;
        existing.Domain = organization.Domain;
        existing.Plan = organization.Plan;
        existing.AzureAdTenantId = organization.AzureAdTenantId;
        existing.StripeCustomerId = organization.StripeCustomerId;
        existing.StripeSubscriptionId = organization.StripeSubscriptionId;
        existing.IsActive = organization.IsActive;
        existing.TrialExpiresAt = organization.TrialExpiresAt;
        existing.Settings = organization.Settings;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated organization: {Name} (ID: {Id})", existing.Name, existing.Id);

        return existing;
    }

    public async Task<List<User>> GetOrganizationUsersAsync(int organizationId)
    {
        return await _dbContext.Users
            .IgnoreQueryFilters()
            .Where(u => u.OrganizationId == organizationId)
            .OrderBy(u => u.LastName)
            .ThenBy(u => u.FirstName)
            .ToListAsync();
    }

    public async Task<User> InviteUserAsync(int organizationId, string email, string role, int invitedByUserId)
    {
        // Verify inviter has permission
        var inviter = await _dbContext.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == invitedByUserId);
        if (inviter == null || !inviter.CanInviteUsers)
        {
            throw new UnauthorizedAccessException("User does not have permission to invite users");
        }

        // Get organization
        var organization = await GetOrganizationByIdAsync(organizationId);
        if (organization == null)
        {
            throw new InvalidOperationException($"Organization with ID {organizationId} not found");
        }

        // Check if user already exists
        var existingUser = await _dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == email && u.OrganizationId == organizationId);

        if (existingUser != null)
        {
            throw new InvalidOperationException($"User with email {email} already exists in this organization");
        }

        // Create new user (they'll set their password on first login or use Azure AD)
        var newUser = new User
        {
            TenantId = organization.TenantId,
            OrganizationId = organization.Id,
            Email = email,
            FirstName = email.Split('@')[0], // Temporary, they can update later
            LastName = "User",
            Role = role,
            PasswordHash = null, // They'll set this on first login or use Azure AD
            CanInviteUsers = role == "Admin",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Users.Add(newUser);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {Email} invited to organization {OrgName} by {InviterEmail}", 
            email, organization.Name, inviter.Email);

        // TODO: Send invitation email

        return newUser;
    }

    public async Task RemoveUserAsync(int organizationId, int userId)
    {
        var user = await _dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId && u.OrganizationId == organizationId);

        if (user == null)
        {
            throw new InvalidOperationException($"User with ID {userId} not found in organization {organizationId}");
        }

        // Don't allow removing the last admin
        var adminCount = await _dbContext.Users
            .IgnoreQueryFilters()
            .CountAsync(u => u.OrganizationId == organizationId && u.Role == "Admin" && u.IsActive);

        if (user.Role == "Admin" && adminCount <= 1)
        {
            throw new InvalidOperationException("Cannot remove the last admin from the organization");
        }

        // Soft delete by marking as inactive
        user.IsActive = false;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {Email} removed from organization {OrgId}", user.Email, organizationId);
    }

    public async Task<bool> CanUserInviteAsync(int userId)
    {
        var user = await _dbContext.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId);
        return user?.CanInviteUsers ?? false;
    }

    public async Task UpdateOrganizationSettingsAsync(int organizationId, string settingsJson)
    {
        var organization = await _dbContext.Organizations.FindAsync(organizationId);
        if (organization == null)
        {
            throw new InvalidOperationException($"Organization with ID {organizationId} not found");
        }

        organization.Settings = settingsJson;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated settings for organization {Name} (ID: {Id})", 
            organization.Name, organization.Id);
    }
}
