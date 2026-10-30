# ADR 002: Organizer-owned runner profiles

- Status: Accepted
- Date: 2026-10-30

## Context

A globally shared Runner table would allow one event organizer to potentially discover or alter another organizer's participant records. It also raises unresolved questions about identity matching, consent, profile ownership and data correction.

## Decision

In the initial release, every Runner belongs to exactly one Organizer. Organizer members can access only runners belonging to organizers where they have an active membership. Runner creation and modification require EventAdministrator or RaceDirector membership (or platform SystemAdministrator).

A runner may be registered in multiple races and events belonging to the same organizer. Cross-organizer sharing, global runner search and automatic profile merging are out of scope.

Registration carries OrganizerId. Composite foreign keys enforce that the referenced Event and Runner belong to that same organizer. The API must still authorize access to the organizer and validate the selected Race, category and wave.

## Consequences

- Add required Runner.OrganizerId and Organizer navigation.
- Add Registration.OrganizerId and composite foreign keys to Event and Runner.
- Scope runner queries by both OrganizerId and RunnerId; never query a runner by ID alone in organizer-facing endpoints.
- Do not expose a global runner directory.
- If cross-organizer registration is required later, design explicit consent and profile-sharing relationships rather than removing tenant boundaries.
