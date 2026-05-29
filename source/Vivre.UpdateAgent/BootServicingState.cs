namespace Vivre.UpdateAgent
{
    /// <summary>
    /// Pure decision for "is Windows servicing busy or a reboot already pending?" — kept free of
    /// any registry / file / COM dependency so it compiles under BOTH the net48 agent and the
    /// net10 test project (linked source). <see cref="BootBusyGuard"/> gathers the real signals;
    /// this just turns them into a go/no-go with a human reason.
    ///
    /// <para>The guard exists because running a WUA or DISM (CBS) operation concurrently with an
    /// in-progress servicing transaction or a pending offline-servicing pass can collide with the
    /// OS's boot-time CSI ResolvePendingTransactions and contribute to a STATUS_SHARING_VIOLATION
    /// rollback. If any signal is set we defer rather than touch the servicing stack.</para>
    /// </summary>
    internal static class BootServicingState
    {
        /// <summary>
        /// Returns whether a servicing operation / reboot is already pending, and a short reason
        /// naming the first signal that tripped. Order is most-specific-first so the reason is useful.
        /// </summary>
        public static (bool Busy, string Reason) Evaluate(
            bool cbsRebootInProgress,
            bool cbsRebootPending,
            bool cbsPackagesPending,
            bool pendingXmlExists,
            bool pendingFileRename,
            bool wuauRebootRequired)
        {
            if (cbsRebootInProgress)
            {
                return (true, "a Component Based Servicing operation is in progress");
            }

            if (pendingXmlExists)
            {
                return (true, "a pending servicing transaction is staged (pending.xml)");
            }

            if (cbsPackagesPending)
            {
                return (true, "Component Based Servicing has packages pending");
            }

            if (cbsRebootPending)
            {
                return (true, "a reboot is pending (Component Based Servicing)");
            }

            if (wuauRebootRequired)
            {
                return (true, "a reboot is required by Windows Update");
            }

            if (pendingFileRename)
            {
                return (true, "a reboot is pending (file-rename operations queued)");
            }

            return (false, string.Empty);
        }
    }
}
