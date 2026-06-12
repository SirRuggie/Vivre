using Xunit;

// Run the whole test assembly serially (no two tests run concurrently).
//
// PSRunspaceHostTests.Cancel_during_execute_phase_throws_OCE_and_leaves_no_unobserved_task_exception
// registers the PROCESS-GLOBAL TaskScheduler.UnobservedTaskException handler and forces a full GC +
// WaitForPendingFinalizers. That inspects faulted-Task finalizers across the entire process, so any other
// test running concurrently — or even this test racing its own abandon-path observe-continuation under
// thread-pool contention — can finalize a transiently-unobserved Task during the GC window and trip the
// assertion. Parallel execution made it flaky as the suite grew. Serial execution removes the contention
// so the canary is deterministic, without weakening what it verifies. The suite is small and fast, so the
// wall-clock cost is negligible.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
