using CloudDentalOffice.Contracts.Prescriptions;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace CloudDentalOffice.Portal.Pages.Prescriptions;

public partial class PrescriptionsPage
{
    // ── State ───────────────────────────────────────────────────────────────

    private bool _loading;
    private bool _showNotifications;
    private bool _showAllergies = true;
    private bool _showMedHistory;
    private bool _showNewRxDialog;
    private bool _showPharmacySearch;
    private bool _showDoseSpot;
    private string _prescriptionFilter = "active";
    private string? _doseSpotSsoUrl;

    private PatientSearchResult? _selectedPatient;
    private PharmacyDto? _selectedPharmacy;
    private PrescriberDto? _currentPrescriber;

    private List<PrescriptionDto> _prescriptions = new();
    private List<PatientAllergyDto> _allergies = new();
    private List<MedicationHistoryDto> _medicationHistory = new();
    private List<ErxNotification> _notifications = new();

    [Parameter] public Guid? PatientId { get; set; }

    private IEnumerable<PrescriptionDto> _filteredPrescriptions =>
        _prescriptionFilter == "active"
            ? _prescriptions.Where(p =>
                p.Status != PrescriptionStatus.Cancelled &&
                p.Status != PrescriptionStatus.Expired)
            : _prescriptions;

    // ── Dental Favorites ────────────────────────────────────────────────────

    private readonly List<DentalRxFavorite> _dentalFavorites = new()
    {
        new("Amoxicillin 500mg", "500mg", "Capsule", "1 cap PO TID x 7 days",
            "Take 1 capsule by mouth 3 times daily for 7 days", 21, 0, 7, "308182"),
        new("Ibuprofen 600mg", "600mg", "Tablet", "1 tab PO Q6H PRN pain",
            "Take 1 tablet by mouth every 6 hours as needed for pain", 20, 0, 5, "197806"),
        new("Amoxicillin/Clavulanate 875mg", "875mg/125mg", "Tablet", "1 tab PO BID x 10 days",
            "Take 1 tablet by mouth twice daily for 10 days", 20, 0, 10, "562251"),
        new("Clindamycin 300mg", "300mg", "Capsule", "1 cap PO QID x 7 days",
            "Take 1 capsule by mouth 4 times daily for 7 days", 28, 0, 7, "197518"),
        new("Acetaminophen/Codeine #3", "300mg/30mg", "Tablet", "1-2 tabs PO Q4-6H PRN",
            "Take 1-2 tablets by mouth every 4-6 hours as needed for pain", 20, 0, 5, "993781",
            DrugSchedule.ScheduleIII),
        new("Chlorhexidine Gluconate 0.12%", "0.12%", "Oral Rinse", "Rinse BID x 30 sec",
            "Rinse with 15mL for 30 seconds twice daily, spit out. Do not swallow.", 1, 0, 14, "310429"),
        new("Peridex (Chlorhexidine)", "0.12%", "Oral Rinse", "Rinse BID",
            "Rinse with 15mL twice daily after brushing. Spit out.", 1, 0, 14, "310429"),
        new("Hydrocodone/Acetaminophen 5/325", "5mg/325mg", "Tablet", "1 tab PO Q4-6H PRN",
            "Take 1 tablet by mouth every 4-6 hours as needed for pain. Do not exceed 6 tablets in 24 hours.", 20, 0, 5, "857002",
            DrugSchedule.ScheduleII)
    };

    // ── Lifecycle ───────────────────────────────────────────────────────────

    protected override async Task OnInitializedAsync()
    {
        // Load current prescriber profile
        // TODO: Get from auth context
        try
        {
            // Placeholder provider ID — in production this comes from the auth token
            var providerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            _currentPrescriber = await PrescriptionService.GetPrescriberAsync(providerId);
        }
        catch
        {
            // Prescriber not yet registered — that's OK
        }

        // If navigated with a patient ID, load that patient
        if (PatientId.HasValue)
        {
            _selectedPatient = new PatientSearchResult
            {
                PatientId = PatientId.Value,
                FirstName = "Loading...",
                LastName = ""
            };
            await LoadPatientData(PatientId.Value);
        }
    }

    // ── Patient Search ──────────────────────────────────────────────────────

    private async Task<IEnumerable<PatientSearchResult>> SearchPatients(string searchText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(searchText) || searchText.Length < 2)
            return Enumerable.Empty<PatientSearchResult>();

        // TODO: Call PatientService via API Gateway
        // For now, return mock data for development
        await Task.Delay(300, ct);

        return new List<PatientSearchResult>
        {
            new() { PatientId = Guid.NewGuid(), FirstName = "John", LastName = "Doe",
                    DateOfBirth = new DateTime(1985, 3, 15), InsurancePlan = "Delta Dental PPO" },
            new() { PatientId = Guid.NewGuid(), FirstName = "Jane", LastName = "Smith",
                    DateOfBirth = new DateTime(1990, 7, 22), InsurancePlan = "Cigna DHMO" },
            new() { PatientId = Guid.NewGuid(), FirstName = "Robert", LastName = "Johnson",
                    DateOfBirth = new DateTime(1978, 11, 8), InsurancePlan = "MetLife" }
        };
    }

    // ── Data Loading ────────────────────────────────────────────────────────

    private async Task LoadPatientData(Guid patientId)
    {
        _loading = true;
        StateHasChanged();

        try
        {
            var tasks = new List<Task>
            {
                LoadPrescriptions(patientId),
                LoadAllergies(patientId)
            };

            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Error loading patient data: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    private async Task LoadPrescriptions(Guid patientId)
    {
        _prescriptions = await PrescriptionService.GetPatientPrescriptionsAsync(
            patientId, includeExpired: true);
    }

    private async Task LoadAllergies(Guid patientId)
    {
        _allergies = await PrescriptionService.GetPatientAllergiesAsync(patientId);
    }

    private async Task LoadMedicationHistory()
    {
        if (_selectedPatient == null) return;

        try
        {
            _medicationHistory = await PrescriptionService.GetMedicationHistoryAsync(
                new GetMedicationHistoryRequest
                {
                    PatientId = _selectedPatient.PatientId,
                    IncludeInactive = true
                });

            _showMedHistory = true;
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Error loading medication history: {ex.Message}", Severity.Error);
        }
    }

    // ── Prescription Actions ────────────────────────────────────────────────

    private void OpenNewPrescription()
    {
        if (_selectedPatient == null)
        {
            Snackbar.Add("Please select a patient first", Severity.Warning);
            return;
        }
        _showNewRxDialog = true;
    }

    private async Task QuickPrescribe(DentalRxFavorite favorite)
    {
        if (_selectedPatient == null || _currentPrescriber == null)
        {
            Snackbar.Add("Please select a patient and ensure prescriber is configured", Severity.Warning);
            return;
        }

        try
        {
            var request = new CreatePrescriptionRequest
            {
                PatientId = _selectedPatient.PatientId,
                PrescriberId = _currentPrescriber.ProviderId,
                DrugName = favorite.DrugName,
                RxNormCode = favorite.RxNormCode,
                Strength = favorite.Strength,
                DoseForm = favorite.DoseForm,
                Directions = favorite.FullDirections,
                Quantity = favorite.Quantity,
                Refills = favorite.Refills,
                DaysSupply = favorite.DaysSupply,
                Schedule = favorite.Schedule,
                PharmacyNcpdpId = _selectedPharmacy?.NcpdpId
            };

            var prescription = await PrescriptionService.CreatePrescriptionAsync(request);
            _prescriptions.Insert(0, prescription);

            Snackbar.Add($"Created: {favorite.DrugName} — Ready to send", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Error creating prescription: {ex.Message}", Severity.Error);
        }
    }

    private async Task SendPrescription(PrescriptionDto prescription)
    {
        try
        {
            var result = await PrescriptionService.SendPrescriptionAsync(
                new SendPrescriptionRequest
                {
                    PrescriptionId = prescription.Id,
                    PharmacyNcpdpId = _selectedPharmacy?.NcpdpId
                });

            // Update the list
            var index = _prescriptions.FindIndex(p => p.Id == prescription.Id);
            if (index >= 0)
                _prescriptions[index] = result;

            Snackbar.Add($"Sent: {prescription.DrugName} to {_selectedPharmacy?.Name ?? "pharmacy"}", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Error sending prescription: {ex.Message}", Severity.Error);
        }
    }

    private async Task CancelPrescription(PrescriptionDto prescription)
    {
        var confirmed = await DialogService.ShowMessageBox(
            "Cancel Prescription",
            $"Cancel {prescription.DrugName} {prescription.Strength}?",
            yesText: "Cancel Rx", cancelText: "Keep");

        if (confirmed != true) return;

        try
        {
            var result = await PrescriptionService.CancelPrescriptionAsync(
                prescription.Id, "Cancelled by prescriber");

            var index = _prescriptions.FindIndex(p => p.Id == prescription.Id);
            if (index >= 0)
                _prescriptions[index] = result;

            Snackbar.Add($"Cancelled: {prescription.DrugName}", Severity.Info);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Error cancelling: {ex.Message}", Severity.Error);
        }
    }

    private void ViewPrescription(PrescriptionDto prescription)
    {
        Navigation.NavigateTo($"/prescriptions/{prescription.Id}");
    }

    // ── Pharmacy ────────────────────────────────────────────────────────────

    private void OpenPharmacySearch() => _showPharmacySearch = true;

    private void HandlePharmacySelected(PharmacyDto pharmacy)
    {
        _selectedPharmacy = pharmacy;
        _showPharmacySearch = false;
        Snackbar.Add($"Pharmacy set: {pharmacy.Name}", Severity.Success);
    }

    // ── DoseSpot ────────────────────────────────────────────────────────────

    private async Task LaunchDoseSpot()
    {
        if (_currentPrescriber == null)
        {
            Snackbar.Add("Prescriber not configured", Severity.Warning);
            return;
        }

        try
        {
            _doseSpotSsoUrl = await PrescriptionService.GetDoseSpotSsoUrlAsync(
                _currentPrescriber.ProviderId,
                _selectedPatient?.PatientId ?? Guid.Empty);

            _showDoseSpot = true;
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Error launching DoseSpot: {ex.Message}", Severity.Error);
        }
    }

    // ── Callbacks ───────────────────────────────────────────────────────────

    private async Task HandlePrescriptionCreated(PrescriptionDto prescription)
    {
        _prescriptions.Insert(0, prescription);
        _showNewRxDialog = false;
        Snackbar.Add($"Created: {prescription.DrugName} — Ready to send", Severity.Success);
    }

    private void DismissNotification(ErxNotification notification)
    {
        _notifications.Remove(notification);
    }

    private void HandleNotificationClick(ErxNotification notification)
    {
        _showNotifications = false;
        if (notification.PrescriptionId != null)
        {
            Navigation.NavigateTo($"/prescriptions/detail/{notification.PrescriptionId}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string TruncateDirections(string directions)
    {
        return directions.Length > 50
            ? directions[..47] + "..."
            : directions;
    }

    [Inject] private IDialogService DialogService { get; set; } = null!;

    // ── View Models ─────────────────────────────────────────────────────────

    public class PatientSearchResult
    {
        public Guid PatientId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public DateTime DateOfBirth { get; set; }
        public string? InsurancePlan { get; set; }
    }

    public record DentalRxFavorite(
        string DrugName,
        string Strength,
        string DoseForm,
        string ShortDirections,
        string FullDirections,
        decimal Quantity,
        int Refills,
        int DaysSupply,
        string? RxNormCode = null,
        DrugSchedule Schedule = DrugSchedule.NonControlled);
}
