@{
    # Severity gate. Information findings are advisory; we fail CI only on
    # Warning and Error.
    Severity = @('Error','Warning')

    # Rules suppressed and why:
    ExcludeRules = @(
        # WinTune is an interactive WPF app, not a pipeline-friendly cmdlet
        # library. State-changing UI-helper functions don't need ShouldProcess.
        'PSUseShouldProcessForStateChangingFunctions',

        # Many catch blocks intentionally swallow errors in UI-event handlers
        # and the dispatcher poll loop -- they exist precisely to keep the
        # window alive when a single tick fails. Documented at the call sites.
        'PSAvoidUsingEmptyCatchBlock',

        # Public function names are part of the dot-source surface advertised
        # in README.md (Get-CleanupTargets, Get-StartupApps, Find-Duplicates,
        # Get-TopProcesses, etc.). Renaming them now would break callers.
        'PSUseSingularNouns',

        # SHA1 is acceptable for personal-file deduplication and the choice is
        # explained in the Dedup.psm1 header. Real-world collision risk on a
        # single user's filesystem is effectively zero.
        'PSAvoidUsingBrokenHashAlgorithms',

        # `Run-Diagnose` in WinTune.ps1 is a local UI helper, not a cmdlet --
        # the prefix communicates "click this to do X" to a script reader.
        'PSUseApprovedVerbs',

        # The Async helper splats $Progress into the user script via @args, so
        # PSSA can't see the binding. The parameter is intentionally present.
        'PSReviewUnusedParameter',

        # Run-Tests.ps1 prints colored section headers to the console. Write-
        # Host is the right tool for that; the rule's complaint about
        # capturability does not apply to a human-facing test runner.
        'PSAvoidUsingWriteHost'
    )
}
