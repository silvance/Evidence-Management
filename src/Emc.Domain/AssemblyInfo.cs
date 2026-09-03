using System.Runtime.CompilerServices;

// Sequencing and hashing are internal so that no production code path can append an event
// without going through IItemEventRecorder (invariant I-07, AUD-008). The test assemblies need
// direct access to construct chains and to simulate a row altered outside the application.
[assembly: InternalsVisibleTo("Emc.Domain.Tests")]
[assembly: InternalsVisibleTo("Emc.Application.Tests")]
