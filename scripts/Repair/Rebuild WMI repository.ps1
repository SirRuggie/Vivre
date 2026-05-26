# Verify the WMI repository; salvage it only if it reports inconsistent. Safe first step for WMI corruption.
$check = (winmgmt /verifyrepository) | Out-String
if ($check -match 'consistent') {
    'WMI repository is consistent. No action needed.'
} else {
    winmgmt /salvagerepository | Out-Null
    'WMI repository was inconsistent - salvage attempted. Re-check: ' + ((winmgmt /verifyrepository) | Out-String).Trim()
}
