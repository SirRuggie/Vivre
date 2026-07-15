# Vivre test fixture - NOT the real WhatsUpGoldPS module.
# Imported through the SAME ISS.ImportPSModule(<path>) seam the production pooled state read uses for
# WhatsUpGoldPS, via the VIVRE_WUG_MODULE_OVERRIDE environment override (test-only; NEVER set in
# production). Its exported functions are shadowed by the per-test resolver-text / worker-tail stubs
# (Get-WUGDevice / Connect-WUGServer), so its only job is to prove the on-disk import-by-path path works
# under the launcher's stripped PSModulePath - without paying the real module's ~8s-per-runspace cold-load.
# Pure ASCII on purpose: this fixture is imported by the ISS directly, never written through the
# UTF-8-with-BOM WritePs51ScriptAsync helper the em-dash-bearing shell-out scripts require.
function Connect-WUGServer { }
function Get-WUGDevice { }
Export-ModuleMember -Function *
