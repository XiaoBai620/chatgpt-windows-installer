from pathlib import Path
import json
import hashlib
import os
import subprocess
import tempfile
import unittest
import zipfile

from build import build


SOURCE_DIRECTORY = Path(__file__).resolve().parent
SCRIPT_HOST = str(Path(os.environ["SystemRoot"]) / "System32" / "cscript.exe")
PACKAGE_MANIFEST = '''<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
<Identity Name="OpenAI.Codex" ProcessorArchitecture="x64" Version="26.1103.7773.0" Publisher="CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B"/>
<Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0"/></Dependencies>
</Package>'''


class InstallerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="chatgpt-installer-test-")
        cls.root = Path(cls.temporary.name) / "中文 路径 & (check)!"
        cls.root.mkdir()
        cls.artifact = build(cls.root)
        cls.library = (SOURCE_DIRECTORY / "installer.js").read_text(encoding="utf-8").split("var exitCode = 1;", 1)[0]

    @classmethod
    def tearDownClass(cls):
        temporary_path = Path(cls.temporary.name)
        cls.temporary.cleanup()
        if temporary_path.exists():
            raise AssertionError("Temporary test directory was not removed")

    def run_script(self, arguments, expected=0):
        result = subprocess.run([SCRIPT_HOST, "//nologo", "//E:JScript", str(self.artifact), *arguments], capture_output=True, timeout=30)
        self.assertEqual(result.returncode, expected, (result.stdout + result.stderr).decode("utf-8", "replace"))
        return result.stdout.decode("utf-8", "replace")

    def run_harness(self, javascript):
        script = self.root / (self.id().rsplit(".", 1)[-1] + ".js")
        wrapped = self.library + '\ntry {\n' + javascript + '\n} catch (error) { WScript.Echo("HARNESS_ERROR: " + error.message); WScript.Quit(1); }\n'
        script.write_text(wrapped, encoding="utf-16")
        result = subprocess.run([SCRIPT_HOST, "//nologo", str(script)], capture_output=True, timeout=30)
        self.assertEqual(result.returncode, 0, (result.stdout + result.stderr).decode("utf-8", "replace"))
        return result.stdout.decode("utf-8", "replace")

    def test_pure_logic(self):
        self.assertIn("SELF_TEST_PASS 19", self.run_script(["--self-test"]))

    def test_read_only_check(self):
        cache = self.root / "must-not-be-created"
        self.assertIn("CHECK_COMPLETE", self.run_script(["--check", "--cache", str(cache)]))
        self.assertFalse(cache.exists())

    def test_actual_bat_entry(self):
        command = f'"{os.environ["COMSPEC"]}" /d /s /c ""{self.artifact}" --self-test"'
        result = subprocess.run(command, capture_output=True, timeout=30)
        self.assertEqual(result.returncode, 0, (result.stdout + result.stderr).decode("utf-8", "replace"))
        self.assertIn(b"SELF_TEST_PASS 19", result.stdout)

    def test_bat_check_argument_order(self):
        cache = self.root / "must-not-be-created-in-bat"
        command = f'"{os.environ["COMSPEC"]}" /d /s /c ""{self.artifact}" --cache "{cache}" --check"'
        result = subprocess.run(command, capture_output=True, timeout=30)
        self.assertEqual(result.returncode, 0, (result.stdout + result.stderr).decode("utf-8", "replace"))
        self.assertIn(b"CHECK_COMPLETE", result.stdout)
        self.assertFalse(cache.exists())

    def test_help_encoding_and_download_launcher(self):
        download = self.root / "ChatGPT-Download.bat"
        command = f'"{os.environ["COMSPEC"]}" /d /s /c ""{download}" --help"'
        result = subprocess.run(command, capture_output=True, timeout=30)
        self.assertEqual(result.returncode, 0)
        # WSH writes redirected stdout using the Windows ANSI code page even
        # when the interactive launcher selects a UTF-8 console.
        try:
            output = result.stdout.decode("utf-8")
        except UnicodeDecodeError:
            output = result.stdout.decode("mbcs")
        self.assertIn("仅下载，不安装", output)
        self.assertIn("--refresh", output)

    def test_download_only_has_no_admin_or_deployment(self):
        cache = self.root / "download-simulation"
        harness = 'options.cache = ' + json.dumps(str(cache)) + ';\n'
        harness += r'''
options.downloadOnly = true;
isAdministrator = function() { fail("ADMIN_CHECK_FOR_DOWNLOAD"); };
checkServices = function() { fail("SERVICE_CHANGE_FOR_DOWNLOAD"); };
waitForOtherDeployment = function() { fail("DEPLOYMENT_WAIT_FOR_DOWNLOAD"); };
runProgram = function() { fail("EXTERNAL_COMMAND_FOR_DOWNLOAD"); };
acquirePackage = function(cache) { return { path: joinPath(cache, "fixture.msix"), manifest: {version: "1.2.3.4"} }; };
acquireLicense = function(cache) { return joinPath(cache, "fixture.xml"); };
if (main() !== 0) { fail("DOWNLOAD_FAILED"); }
if (eventLog) { eventLog.Close(); eventLog = null; }
if (ownedLock) { fileSystem.DeleteFile(ownedLock, false); ownedLock = ""; }
WScript.Echo("DOWNLOAD_ROUTING_PASS");
'''
        self.assertIn("DOWNLOAD_ROUTING_PASS", self.run_harness(harness))

    def test_refresh_preserves_cache_and_uses_official_sources(self):
        directory = self.root / "refresh-cache"
        directory.mkdir()
        package = directory / "ChatGPT.msix"
        package.write_bytes(b"original-cached-package")
        license_path = directory / "ChatGPT-License-20261112-120000-1.xml"
        license_path.write_text('<License/>', encoding="utf-8")
        harness = 'var cache = ' + json.dumps(str(directory)) + ';\n'
        harness += r'''
inspectPackage = function() { return { version: "1.2.3.4" }; };
download = function() { fail("CACHE_SHOULD_BE_REUSED"); };
if (acquireLicense(cache).indexOf("20261112") < 0) { fail("TIMESTAMP_LICENSE_NOT_REUSED"); }
options.refresh = true;
var requests = [];
download = function(url, destination, label) {
    if (url.indexOf(officialBase) !== 0) { fail("NON_OFFICIAL_SOURCE"); }
    if (fileSystem.FileExists(destination)) { fail("CACHE_OVERWRITE"); }
    requests.push(url);
    var output = fileSystem.CreateTextFile(destination, false, false);
    output.WriteLine(label === "download-license" ? "<License/>" : "fixture");
    output.Close();
};
var fresh = acquirePackage(cache, "x64");
var freshLicense = acquireLicense(cache);
if (fresh.path === joinPath(cache, "ChatGPT.msix")) { fail("OLD_PACKAGE_REUSED"); }
if (requests.length !== 2 || requests[0] !== officialBase + "ChatGPT-x64.msix" ||
    requests[1] !== officialBase + "ChatGPT-License.xml") { fail("WRONG_DOWNLOADS"); }
WScript.Echo("REFRESH_PASS");
'''
        self.assertIn("REFRESH_PASS", self.run_harness(harness))
        self.assertEqual(package.read_bytes(), b"original-cached-package")
        self.assertEqual(license_path.read_text(encoding="utf-8"), '<License/>')

    def test_reuse_cache_without_download_environment(self):
        directory = self.root / "existing-downloads"
        directory.mkdir()
        package = directory / "ChatGPT.msix"
        package.write_bytes(b"existing-user-package-preserved")
        license_path = directory / "ChatGPT-License.xml"
        license_path.write_text('<License xmlns="urn:schemas-microsoft-com:windows:store:licensing:ls"/>', encoding="utf-8")
        script = 'ensureSystemTool = function() { fail("UNNECESSARY_DOWNLOAD_TOOL"); };\n'
        script += 'inspectPackage = function() { return { version: "26.1103.7773.0" }; };\n'
        script += 'download = function() { fail("UNNECESSARY_DOWNLOAD"); };\n'
        script += 'var cache = ' + json.dumps(str(directory)) + ';\n'
        script += 'if (acquirePackage(cache, "x64").path !== joinPath(cache, "ChatGPT.msix")) { fail("CACHE_MISSING"); }\n'
        script += 'if (acquireLicense(cache) !== joinPath(cache, "ChatGPT-License.xml")) { fail("LICENSE_MISSING"); }\n'
        script += 'WScript.Echo("CACHE_REUSE_PASS");\n'
        self.assertIn("CACHE_REUSE_PASS", self.run_harness(script))
        self.assertEqual(package.read_bytes(), b"existing-user-package-preserved")

    def test_shell_process_and_manifest(self):
        directory = self.root / "process & 中文 (files)!"
        directory.mkdir()
        package = directory / "ChatGPT.msix"
        with zipfile.ZipFile(package, "w", zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("AppxManifest.xml", PACKAGE_MANIFEST)
        exit_script = directory / "exit-code.js"
        exit_script.write_text("WScript.Quit(37);", encoding="ascii")
        script = "logDirectory = " + json.dumps(str(directory)) + ";\n"
        script += 'var result = runProgram(joinPath(systemDirectory, "curl.exe"), ["--version"], "curl-version");\n'
        script += 'if (result.code !== 0 || result.text.indexOf("curl ") < 0) { fail("PROCESS_TEST_FAILED"); }\n'
        script += 'result = runProgram(joinPath(systemDirectory, "cscript.exe"), ["//nologo", ' + json.dumps(str(exit_script)) + '], "exit-code");\n'
        script += 'if (result.code !== 37) { fail("EXIT_CODE_NOT_PRESERVED"); }\n'
        script += "var metadata = inspectPackage(" + json.dumps(str(package)) + ', "x64");\n'
        script += 'if (metadata.version !== "26.1103.7773.0") { fail("MANIFEST_TEST_FAILED"); }\n'
        script += 'WScript.Echo("PROCESS_TEST_PASS");\n'
        self.assertIn("PROCESS_TEST_PASS", self.run_harness(script))

    def test_installation_state_machine_without_installing(self):
        base = self.root / "simulated"
        base.mkdir()
        harness = "var simulationRoot = " + json.dumps(str(base)) + ";\n"
        harness += r'''
waitForOtherDeployment = function() {};
checkServices = function() {};
windowsVersion = function() { return "10.0.22631.6060"; };
acquirePackage = function(cache) { return { path: joinPath(cache, "fixture.msix"), manifest: {
    version: "26.1103.7773.0", minimumWindows: "10.0.19041.0", dependencies: [] } }; };
acquireLicense = function(cache) { return joinPath(cache, "fixture.xml"); };
collectDeploymentEvents = function() {};
var scenarios = ["fresh", "already", "failed", "repair", "repair-failed", "verification-failed", "restart"];
for (var scenarioIndex = 0; scenarioIndex < scenarios.length; scenarioIndex++) {
    var scenario = scenarios[scenarioIndex];
    var inventoryCalls = 0;
    var installCalls = 0;
    var repairs = 0;
    provisionedPackages = function() {
        inventoryCalls++;
        var present = scenario === "already" || (inventoryCalls > 1 && scenario !== "verification-failed");
        return { code: 0, text: present ? "PackageName : OpenAI.Codex_26.1103.7773.0_x64__2p2nqsd0c76g0" : "", log: "simulation" };
    };
    runProgram = function(executable, argumentsList, label) {
        if (label === "repair-windows") {
            repairs++;
            return { code: scenario === "repair-failed" ? 1 : 0, text: "repair result", log: "simulation" };
        }
        if (label !== "deploy-chatgpt" && label !== "retry-deployment") { fail("UNEXPECTED_COMMAND_" + label); }
        installCalls++;
        if (scenario === "failed") { return { code: 1, text: "0x80073cf3", log: "simulation" }; }
        if ((scenario === "repair" || scenario === "repair-failed") && installCalls === 1) {
            return { code: 1, text: "0x80073712", log: "simulation" };
        }
        return { code: scenario === "restart" ? 3010 : 0, text: "", log: "simulation" };
    };
    var failed = false;
    try { runInstallation(joinPath(simulationRoot, scenario), "x64"); }
    catch (error) { failed = true; }
    finally {
        if (eventLog) { eventLog.Close(); eventLog = null; }
        if (ownedLock) { fileSystem.DeleteFile(ownedLock, false); ownedLock = ""; }
    }
    var shouldFail = scenario === "failed" || scenario === "repair" || scenario === "repair-failed" || scenario === "verification-failed";
    if (failed !== shouldFail) { fail("SCENARIO_RESULT_" + scenario); }
    if (scenario === "already" && installCalls !== 0) { fail("REPEATED_INSTALL"); }
    if (repairs !== 0) { fail("AUTOMATIC_REPAIR_FORBIDDEN"); }
    if ((scenario === "repair" || scenario === "repair-failed") && installCalls !== 1) { fail("UNEXPECTED_RETRY"); }
}
WScript.Echo("SIMULATION_PASS 7");
'''
        self.assertIn("SIMULATION_PASS 7", self.run_harness(harness))

    def test_release_contents(self):
        data = self.artifact.read_bytes()
        self.assertTrue(data.isascii())
        self.assertNotIn(b"\n", data.replace(b"\r\n", b""))
        self.assertNotIn(b"powershell.exe", data.lower())
        with zipfile.ZipFile(self.root / "ChatGPT-Offline-Helper-v1.1.0.zip") as archive:
            self.assertEqual(set(archive.namelist()), {"ChatGPT-Install.bat", "ChatGPT-Download.bat", "README.md", "LICENSE", "SHA256SUMS.txt", "docs/download-analysis.md", "docs/validation.md"})
            self.assertEqual(archive.read("ChatGPT-Install.bat"), data)
            for line in archive.read("SHA256SUMS.txt").decode("ascii").splitlines():
                checksum, name = line.split("  ", 1)
                self.assertEqual(hashlib.sha256(archive.read(name)).hexdigest(), checksum)
        self.assertNotIn(b"/RestoreHealth", data)
        self.assertNotIn(b"StartService", data)


if __name__ == "__main__":
    unittest.main(verbosity=2)
