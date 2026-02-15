namespace CloudDentalOffice.Contracts.Patients;

// ── Patient ──

public record PatientDto
{
    public int PatientId { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string? MiddleName { get; init; }
    public string? PreferredName { get; init; }
    public DateTime DateOfBirth { get; init; }
    public string Gender { get; init; } = string.Empty;
    public string? SSN { get; init; }
    public string? Email { get; init; }
    public string? PrimaryPhone { get; init; }
    public string? SecondaryPhone { get; init; }
    public string? Address1 { get; init; }
    public string? Address2 { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? ZipCode { get; init; }
    public string Status { get; init; } = "Active";
    public DateTime CreatedDate { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public List<PatientInsuranceDto> Insurances { get; init; } = [];

    // Computed
    public string FullName => $"{FirstName} {LastName}";
    public int Age
    {
        get
        {
            var today = DateTime.Today;
            var age = today.Year - DateOfBirth.Year;
            if (DateOfBirth.Date > today.AddYears(-age)) age--;
            return age;
        }
    }
}

public record CreatePatientRequest
{
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string? MiddleName { get; init; }
    public string? PreferredName { get; init; }
    public DateTime DateOfBirth { get; init; }
    public string Gender { get; init; } = "U";
    public string? SSN { get; init; }
    public string? Email { get; init; }
    public string? PrimaryPhone { get; init; }
    public string? SecondaryPhone { get; init; }
    public string? Address1 { get; init; }
    public string? Address2 { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? ZipCode { get; init; }
}

public record UpdatePatientRequest
{
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? MiddleName { get; init; }
    public string? PreferredName { get; init; }
    public DateTime? DateOfBirth { get; init; }
    public string? Gender { get; init; }
    public string? SSN { get; init; }
    public string? Email { get; init; }
    public string? PrimaryPhone { get; init; }
    public string? SecondaryPhone { get; init; }
    public string? Address1 { get; init; }
    public string? Address2 { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? ZipCode { get; init; }
    public string? Status { get; init; }
}

// ── Patient Insurance ──

public record PatientInsuranceDto
{
    public int PatientInsuranceId { get; init; }
    public int PatientId { get; init; }
    public int InsurancePlanId { get; init; }
    public string MemberId { get; init; } = string.Empty;
    public string? GroupNumber { get; init; }
    public int SequenceNumber { get; init; }
    public DateTime EffectiveDate { get; init; }
    public DateTime? TerminationDate { get; init; }
    public bool IsActive { get; init; }
    public string? RelationshipToSubscriber { get; init; }
    public string? SubscriberFirstName { get; init; }
    public string? SubscriberLastName { get; init; }
    public DateTime? SubscriberDateOfBirth { get; init; }
    public InsurancePlanDto? InsurancePlan { get; init; }
}

public record CreatePatientInsuranceRequest
{
    public int PatientId { get; init; }
    public int InsurancePlanId { get; init; }
    public string MemberId { get; init; } = string.Empty;
    public string? GroupNumber { get; init; }
    public int SequenceNumber { get; init; } = 1;
    public DateTime EffectiveDate { get; init; }
    public DateTime? TerminationDate { get; init; }
    public string? RelationshipToSubscriber { get; init; }
    public string? SubscriberFirstName { get; init; }
    public string? SubscriberLastName { get; init; }
    public DateTime? SubscriberDateOfBirth { get; init; }
}

// ── Insurance Plan ──

public record InsurancePlanDto
{
    public int InsurancePlanId { get; init; }
    public string PayerId { get; init; } = string.Empty;
    public string PayerName { get; init; } = string.Empty;
    public string? PlanName { get; init; }
    public string? PlanType { get; init; }
    public string? Phone { get; init; }
    public string? Address1 { get; init; }
    public string? Address2 { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? ZipCode { get; init; }
    public string? EdiPayerId { get; init; }
    public bool EdiEnabled { get; init; }
    public string? EdiSubmissionType { get; init; }
    public bool IsActive { get; init; }
}

public record CreateInsurancePlanRequest
{
    public string PayerId { get; init; } = string.Empty;
    public string PayerName { get; init; } = string.Empty;
    public string? PlanName { get; init; }
    public string? PlanType { get; init; }
    public string? Phone { get; init; }
    public string? Address1 { get; init; }
    public string? Address2 { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? ZipCode { get; init; }
    public string? EdiPayerId { get; init; }
    public bool EdiEnabled { get; init; }
    public string? EdiSubmissionType { get; init; }
}
