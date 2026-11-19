namespace RaceTiming.Domain.Enums;

public enum RecordStatus { Active = 0, Inactive = 1 }
public enum RegistrationStatus { Pending = 0, Confirmed = 1, Waitlisted = 2, Cancelled = 3, Dnf = 4, Dns = 5, Disqualified = 6 }
public enum PaymentStatus { Unspecified, Unpaid, Paid, Waived, Refunded }
public enum OrganizerRole { EventAdministrator = 0, RaceDirector = 1, TimingOfficial = 2, CheckpointMarshal = 3, SafetyOfficer = 4 }
