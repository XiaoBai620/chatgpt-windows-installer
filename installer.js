// ACE64 community helper. Downloads official OpenAI packages; not an OpenAI product.
var fileSystem = new ActiveXObject("Scripting.FileSystemObject");
var commandShell = new ActiveXObject("WScript.Shell");
var windowsDirectory = commandShell.ExpandEnvironmentStrings("%SystemRoot%");
var systemDirectory = windowsDirectory + "\\System32";
var expectedPublisher = "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B";
var officialBase = "https://persistent.oaistatic.com/codex-app-prod/";
var logDirectory = "";
var eventLog = null;
var operationNumber = 0;
var ownedLock = "";
var transientFiles = [];
var options = { check: false, selfTest: false, elevated: false, cache: "", refresh: false,
    noPause: false, downloadOnly: /ChatGPT-Download\.bat$/i.test(WScript.ScriptFullName) };

function message(text) {
    WScript.Echo(text);
    if (eventLog) { eventLog.WriteLine(new Date().toLocaleString() + " " + text); }
}

function fail(text) { throw new Error(text); }
function joinPath(parent, child) { return fileSystem.BuildPath(parent, child); }

function quote(value) {
    value = String(value);
    if (/["\r\n%]/.test(value)) {
        fail("路径或参数包含不支持的字符（双引号、百分号或换行）。请换到普通文件夹运行。");
    }
    return '"' + value.replace(/(\\+)$/, "$1$1") + '"';
}

function versionCompare(left, right) {
    var leftParts = String(left).split(".");
    var rightParts = String(right).split(".");
    for (var index = 0; index < Math.max(leftParts.length, rightParts.length); index++) {
        var difference = Number(leftParts[index] || 0) - Number(rightParts[index] || 0);
        if (difference) { return difference > 0 ? 1 : -1; }
    }
    return 0;
}

function timestamp() {
    var now = new Date();
    function pad(value) { return value < 10 ? "0" + value : String(value); }
    return now.getFullYear() + pad(now.getMonth() + 1) + pad(now.getDate()) + "-" +
        pad(now.getHours()) + pad(now.getMinutes()) + pad(now.getSeconds()) + "-" + now.getMilliseconds();
}

function ensureDirectory(path) {
    if (!fileSystem.FolderExists(path)) {
        var parent = fileSystem.GetParentFolderName(path);
        if (parent && !fileSystem.FolderExists(parent)) { ensureDirectory(parent); }
        fileSystem.CreateFolder(path);
    }
}

function registryValue(path, fallback) {
    try { return commandShell.RegRead(path); } catch (error) { return fallback; }
}

function architecture() {
    var environment = commandShell.Environment("PROCESS");
    var value = String(environment("PROCESSOR_ARCHITEW6432") || environment("PROCESSOR_ARCHITECTURE")).toUpperCase();
    if (value === "AMD64") { return "x64"; }
    if (value === "ARM64") { return "arm64"; }
    fail("本工具只支持 64 位 Windows（x64 或 Arm64）。检测值：" + value);
}

function windowsVersion() {
    var key = "HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\";
    return "10.0." + registryValue(key + "CurrentBuildNumber", "0") + "." + registryValue(key + "UBR", "0");
}

function isAdministrator() {
    return commandShell.Run(quote(joinPath(systemDirectory, "fltmc.exe")), 0, true) === 0;
}

function readText(path) {
    var stream = new ActiveXObject("ADODB.Stream");
    stream.Type = 2;
    stream.Charset = "utf-8";
    stream.Open();
    stream.LoadFromFile(path);
    var text = stream.ReadText();
    stream.Close();
    return text.replace(/\x00/g, "");
}

function parseXml(text) {
    var document = new ActiveXObject("Msxml2.DOMDocument.6.0");
    document.async = false;
    document.resolveExternals = false;
    document.validateOnParse = false;
    document.setProperty("ProhibitDTD", true);
    if (!document.loadXML(text.replace(/^﻿/, ""))) { fail("XML 文件不完整或格式错误：" + document.parseError.reason); }
    return document;
}

function parseManifest(text, wantedArchitecture) {
    var document = parseXml(text);
    var identity = document.selectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']");
    if (!identity || identity.getAttribute("Name") !== "OpenAI.Codex" || identity.getAttribute("Publisher") !== expectedPublisher) {
        fail("安装包的名称或发布者与当前官方 ChatGPT 安装包不符，已停止。");
    }
    if (identity.getAttribute("ProcessorArchitecture") !== wantedArchitecture) { fail("安装包架构与这台电脑不一致。"); }
    var target = document.selectSingleNode("//*[local-name()='TargetDeviceFamily' and @Name='Windows.Desktop']");
    if (!target) { fail("安装包缺少 Windows 桌面系统要求。"); }
    if (!/^\d+\.\d+\.\d+\.\d+$/.test(identity.getAttribute("Version")) ||
        !/^\d+\.\d+\.\d+\.\d+$/.test(target.getAttribute("MinVersion"))) { fail("安装包版本字段无效。"); }
    var dependencies = document.selectNodes("//*[local-name()='PackageDependency' or local-name()='MainPackageDependency' or local-name()='HostRuntimeDependency']");
    var dependencyNames = [];
    for (var index = 0; index < dependencies.length; index++) {
        dependencyNames.push(dependencies.item(index).getAttribute("Name") || dependencies.item(index).nodeName);
    }
    return { name: identity.getAttribute("Name"), version: identity.getAttribute("Version"),
        architecture: wantedArchitecture, minimumWindows: target.getAttribute("MinVersion"), dependencies: dependencyNames };
}

function runProgram(executable, argumentsList, label) {
    operationNumber++;
    var outputFile = joinPath(logDirectory, (operationNumber < 10 ? "0" : "") + operationNumber + "-" + label + ".log");
    var parts = [quote(executable)];
    for (var index = 0; index < argumentsList.length; index++) { parts.push(quote(argumentsList[index])); }
    var innerCommand = parts.join(" ") + " > " + quote(outputFile) + " 2>&1";
    var process = commandShell.Exec(quote(joinPath(systemDirectory, "cmd.exe")) + ' /d /v:off /s /c "' + innerCommand + '"');
    var started = new Date().getTime();
    var lastNotice = started;
    while (process.Status === 0) {
        WScript.Sleep(250);
        var current = new Date().getTime();
        if (current - lastNotice >= 15000) {
            message("仍在执行 " + label + "，已用时 " + Math.floor((current - started) / 1000) + " 秒。日志：" + outputFile);
            lastNotice = current;
        }
    }
    var text = fileSystem.FileExists(outputFile) ? readText(outputFile) : "";
    message(label + "：退出码 " + process.ExitCode);
    return { code: process.ExitCode, text: text, log: outputFile };
}

function displayFailure(result) {
    var lines = result.text.replace(/\r/g, "").split("\n");
    message(lines.slice(Math.max(0, lines.length - 18)).join("\n"));
    message("本步骤日志：" + result.log);
}

function servicingArguments(argumentsList, label) {
    return ["/English", "/Online", "/NoRestart", "/LogPath:" + joinPath(logDirectory, label + "-dism.log")].concat(argumentsList);
}

function successful(code) { return code === 0 || code === 3010; }

function needsComponentRepair(result) {
    return /0x80073712|0x800f0831/i.test(result.text) ||
        (result.code >>> 0) === 0x80073712 || (result.code >>> 0) === 0x800f0831;
}

function repairWindows(reason) {
    fail("Windows 组件检查未通过：" + reason + "。请联系系统管理员或云电脑平台处理后重试；本工具不自动修复系统。");
}

function waitForOtherDeployment() {
    var management;
    try { management = GetObject("winmgmts:{impersonationLevel=impersonate}!\\\\.\\root\\cimv2"); }
    catch (error) { fail("无法检查正在运行的部署任务（WMI 不可用）。请先让云电脑平台恢复 Windows 管理组件。"); }
    for (var attempt = 0; attempt < 13; attempt++) {
        var processes = new Enumerator(management.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name='dism.exe'"));
        if (processes.atEnd()) { return; }
        if (attempt === 12) { fail("另一项 DISM 操作仍在运行。请等待你之前的安装窗口结束，再重新运行本工具。不会终止已有任务。"); }
        message("检测到另一项 DISM 操作，等待其结束（最多等待两分钟）……");
        WScript.Sleep(10000);
    }
}

function checkServices() {
    var management = GetObject("winmgmts:{impersonationLevel=impersonate}!\\\\.\\root\\cimv2");
    var required = ["AppXSvc", "ClipSVC", "StateRepository", "CryptSvc"];
    for (var index = 0; index < required.length; index++) {
        var service;
        try { service = management.Get("Win32_Service.Name='" + required[index] + "'"); }
        catch (error) {
            if ((error.number >>> 0) !== 0x80041002) { fail("无法查询系统服务 " + required[index] + "：" + error.message); }
            repairWindows("缺少系统服务 " + required[index]);
        }
        message("系统服务 " + required[index] + "：" + service.State + " / " + service.StartMode);
        if (String(service.StartMode).toLowerCase() === "disabled") {
            fail("系统服务 " + required[index] + " 被禁用。请由云电脑平台确认策略并恢复该服务，本工具不会覆盖管理策略。");
        }
        // Windows deployment starts its own services when required; do not change service policy here.
    }
}

function inspectPackage(path, wantedArchitecture) {
    var result = runProgram(joinPath(systemDirectory, "tar.exe"), ["-xOf", path, "AppxManifest.xml"], "read-manifest");
    if (result.code !== 0) { fail("无法读取安装包清单，文件可能不完整：" + path); }
    return parseManifest(result.text, wantedArchitecture);
}

function ensureSystemTool(name) {
    if (!fileSystem.FileExists(joinPath(systemDirectory, name))) {
        repairWindows("缺少 Windows 自带工具 " + name);
    }
}

function download(url, destination, label) {
    ensureSystemTool("curl.exe");
    var partial = destination + ".part";
    transientFiles.push(partial);
    var argumentsList = ["--fail", "--location", "--proto", "=https", "--proto-redir", "=https",
        "--retry", "2", "--connect-timeout", "30", "--max-time", "3600", "--speed-limit", "1024", "--speed-time", "120",
        "--output", partial, url];
    var result = runProgram(joinPath(systemDirectory, "curl.exe"), argumentsList, label);
    if (result.code !== 0 || !fileSystem.FileExists(partial) || fileSystem.GetFile(partial).Size === 0) {
        displayFailure(result);
        fail("官方文件下载失败。检查云电脑联网情况后重试；已有完整安装包不会被删除。");
    }
    if (fileSystem.FileExists(destination)) { fail("目标文件已经存在，停止覆盖：" + destination); }
    fileSystem.MoveFile(partial, destination);
}

function acquirePackage(cache, wantedArchitecture) {
    var candidates = [joinPath(cache, "ChatGPT.msix"), joinPath(cache, "ChatGPT-" + wantedArchitecture + ".msix")];
    var cacheFiles = new Enumerator(fileSystem.GetFolder(cache).Files);
    for (; !cacheFiles.atEnd(); cacheFiles.moveNext()) {
        var candidate = cacheFiles.item();
        if (new RegExp("^ChatGPT-" + wantedArchitecture + "-[0-9-]+\\.msix$", "i").test(candidate.Name)) { candidates.push(candidate.Path); }
    }
    var best = null;
    for (var index = 0; index < candidates.length; index++) {
        if (fileSystem.FileExists(candidates[index])) {
            try {
                var manifest = inspectPackage(candidates[index], wantedArchitecture);
                if (!best || versionCompare(manifest.version, best.manifest.version) > 0) { best = { path: candidates[index], manifest: manifest }; }
            } catch (error) { message("保留但不使用已有文件：" + candidates[index] + "；" + error.message); }
        }
    }
    if (best && !options.refresh) {
        message("复用已下载的官方格式安装包：" + best.path + "，版本 " + best.manifest.version + "。Windows 会在部署时校验签名。");
        return best;
    }
    var destination = joinPath(cache, "ChatGPT-" + wantedArchitecture + "-" + timestamp() + ".msix");
    message("正在从 OpenAI 官方地址下载安装包，文件约 1 GB，请耐心等待。");
    download(officialBase + "ChatGPT-" + wantedArchitecture + ".msix", destination, "download-chatgpt");
    return { path: destination, manifest: inspectPackage(destination, wantedArchitecture) };
}

function validLicense(path) {
    try {
        var document = parseXml(readText(path));
        return document.documentElement && document.documentElement.baseName === "License";
    } catch (error) { return false; }
}

function acquireLicense(cache) {
    var files = new Enumerator(fileSystem.GetFolder(cache).Files);
    for (; !files.atEnd(); files.moveNext()) {
        var candidate = files.item();
        if (/^ChatGPT-License(?:-[0-9-]+)?\.xml$/i.test(candidate.Name) && validLicense(candidate.Path) && !options.refresh) {
            message("复用已有离线许可证。");
            return candidate.Path;
        }
    }
    var destination = joinPath(cache, "ChatGPT-License-" + timestamp() + ".xml");
    download(officialBase + "ChatGPT-License.xml", destination, "download-license");
    if (!validLicense(destination)) { fail("官方许可证不是有效的 License XML，停止部署。"); }
    return destination;
}

function findProvisionedVersion(text, wantedArchitecture) {
    var expression = /OpenAI\.Codex_([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)_(x64|arm64)__2p2nqsd0c76g0\b/ig;
    var highest = "";
    var match;
    while ((match = expression.exec(text)) !== null) {
        if (match[2].toLowerCase() === wantedArchitecture && (!highest || versionCompare(match[1], highest) > 0)) { highest = match[1]; }
    }
    return highest;
}

function provisionedPackages() {
    return runProgram(joinPath(systemDirectory, "dism.exe"),
        servicingArguments(["/Get-ProvisionedAppxPackages"], "inventory"), "check-provisioning");
}

function collectDeploymentEvents() {
    try {
        runProgram(joinPath(systemDirectory, "wevtutil.exe"), ["qe", "Microsoft-Windows-AppXDeploymentServer/Operational",
            "/c:15", "/rd:true", "/f:text"], "appx-events");
    } catch (error) { message("无法读取 AppX 事件日志：" + error.message); }
}

function describeInstallError(result) {
    var text = result.text;
    if (/0x80073d02/i.test(text)) { return "ChatGPT 或相关进程正在使用文件。保存工作并关闭相关应用后重试。"; }
    if (/0x80073cf3/i.test(text)) { return "Windows 报告依赖、架构或包冲突，请查看 AppX 事件日志；不会猜测并安装无关运行库。"; }
    if (/0x80073d28|0x80070005/i.test(text)) { return "Windows 拒绝部署权限。请由云电脑平台检查管理员权限及应用安装策略。"; }
    if (/0x800f081f|0x800f0906/i.test(text)) { return "Windows 缺少可用修复源，或无法连接 Windows Update。需要平台提供匹配的系统修复源。"; }
    return "部署没有成功完成。请把本工具日志目录中的 summary.log 和失败步骤日志发给排障人员。";
}

function checkOnly(cache) {
    message("只读检查：不会下载安装、修改设置或请求管理员权限。");
    message("系统版本：" + windowsVersion() + "；架构：" + architecture());
    message("计划缓存目录：" + cache);
    var tools = ["cscript.exe", "curl.exe", "tar.exe", "dism.exe", "fltmc.exe"];
    for (var index = 0; index < tools.length; index++) {
        message(tools[index] + "：" + (fileSystem.FileExists(joinPath(systemDirectory, tools[index])) ? "存在" : "缺少"));
    }
    message("已有安装包：" + (fileSystem.FileExists(joinPath(cache, "ChatGPT.msix")) ? "是" : "否"));
    message("已有许可证：" + (fileSystem.FileExists(joinPath(cache, "ChatGPT-License.xml")) ? "是" : "否"));
    message("CHECK_COMPLETE");
}

function selfTest() {
    var count = 0;
    function assert(condition, name) { count++; if (!condition) { fail("SELF_TEST_FAILED: " + name); } }
    function rejected(operation) { try { operation(); return false; } catch (error) { return true; } }
    var manifest = '<Package><Identity Name="OpenAI.Codex" Publisher="' + expectedPublisher +
        '" ProcessorArchitecture="x64" Version="26.1103.7773.0"/><Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0"/></Dependencies></Package>';
    assert(versionCompare("26.9.0.0", "26.10.0.0") < 0, "numeric version ordering");
    assert(versionCompare("1.2", "1.2.0.0") === 0, "short versions");
    assert(quote("C:\\用户\\A & B\\file.msix") === '"C:\\用户\\A & B\\file.msix"', "spaces and metacharacters");
    assert(rejected(function() { quote("a%PATH%b"); }), "environment expansion rejected");
    assert(rejected(function() { quote('a" & echo unsafe'); }), "quote injection rejected");
    assert(rejected(function() { quote("a\r\nb"); }), "line injection rejected");
    assert(parseManifest(manifest, "x64").dependencies.length === 0, "self contained manifest");
    assert(rejected(function() { parseManifest(manifest, "arm64"); }), "architecture mismatch");
    assert(rejected(function() { parseManifest(manifest.replace(expectedPublisher, "CN=Other"), "x64"); }), "publisher mismatch");
    assert(rejected(function() { parseManifest(manifest.replace("OpenAI.Codex", "Other.App"), "x64"); }), "identity mismatch");
    assert(parseManifest(manifest.replace("</Dependencies>", '<PackageDependency Name="Example.Framework"/></Dependencies>'), "x64").dependencies[0] === "Example.Framework", "new dependency detected");
    assert(rejected(function() { parseXml('<!DOCTYPE a [<!ENTITY bad SYSTEM "file:///missing">]><a>&bad;</a>'); }), "external entities rejected");
    assert(findProvisionedVersion("PackageName : OpenAI.Codex_26.1103.7773.0_x64__2p2nqsd0c76g0", "x64") === "26.1103.7773.0", "provisioning verification");
    assert(findProvisionedVersion("PackageName : OpenAI.Codex_26.1103.7773.0_arm64__2p2nqsd0c76g0", "x64") === "", "wrong architecture excluded");
    assert(findProvisionedVersion("PackageName : OpenAI.Codex_26.1103.7773.0_x64__otherpublisher", "x64") === "", "wrong package family excluded");
    assert(rejected(function() { parseManifest(manifest.replace('MinVersion="10.0.19041.0"', 'MinVersion="bad"'), "x64"); }), "invalid minimum version");
    assert(needsComponentRepair({ code: 0, text: "Error: 0x80073712" }), "component store corruption");
    assert(!needsComponentRepair({ code: 1, text: "0x80073cf3" }), "dependency failure does not trigger repair");
    assert(successful(3010) && !successful(5), "restart and failure codes");
    message("SELF_TEST_PASS " + count);
}

function beginSession(cache, wantedArchitecture) {
    ensureDirectory(cache);
    var lockPath = joinPath(cache, ".chatgpt-installer.lock");
    try {
        var lock = fileSystem.CreateTextFile(lockPath, false, true);
        lock.WriteLine("Created " + new Date().toLocaleString());
        lock.Close();
        ownedLock = lockPath;
    } catch (error) {
        fail("缓存目录已有安装锁，或无法写入。请先确认没有另一个工具在运行；异常退出留下的锁可在确认后手动移除：" + lockPath);
    }
    logDirectory = joinPath(cache, "logs\\" + timestamp());
    ensureDirectory(logDirectory);
    eventLog = fileSystem.CreateTextFile(joinPath(logDirectory, "summary.log"), false, true);
    message("ACE64 ChatGPT 离线下载与安装助手 v1.1.0（社区工具）");
    message("系统：" + windowsVersion() + "；架构：" + wantedArchitecture);
    message("日志目录：" + logDirectory);
}

function runDownload(cache, wantedArchitecture) {
    beginSession(cache, wantedArchitecture);
    var drive = fileSystem.GetDrive(fileSystem.GetDriveName(cache));
    if (Number(drive.FreeSpace) < 2 * 1024 * 1024 * 1024) { fail("下载盘至少需要 2 GB 可用空间。"); }
    var package = acquirePackage(cache, wantedArchitecture);
    var license = acquireLicense(cache);
    message("DOWNLOAD_COMPLETE");
    message("下载完成：" + package.path);
    message("版本：" + package.manifest.version + "；架构：" + wantedArchitecture);
    message("许可证：" + license);
    message("本次仅下载，没有安装。把整个下载目录保留或复制到目标电脑，再运行 ChatGPT-Install.bat --cache 指定此目录。");
    message("清单信息已核对；Windows 会在实际安装时校验 MSIX 签名。离线安装不代表离线使用，应用仍需联网。");
    return 0;
}

function runInstallation(cache, wantedArchitecture) {
    waitForOtherDeployment();
    beginSession(cache, wantedArchitecture);
    if (versionCompare(windowsVersion(), "10.0.19041.0") < 0) { fail("系统版本低于当前官方安装包要求，请先升级 Windows。"); }
    var cacheDrive = fileSystem.GetDrive(fileSystem.GetDriveName(cache));
    var windowsDrive = fileSystem.GetDrive(fileSystem.GetDriveName(windowsDirectory));
    if (Number(cacheDrive.FreeSpace) < 2 * 1024 * 1024 * 1024 || Number(windowsDrive.FreeSpace) < 6 * 1024 * 1024 * 1024) {
        fail("可用空间不足。本工具建议缓存盘至少 2 GB、Windows 系统盘至少 6 GB 可用空间。");
    }
    ensureSystemTool("tar.exe");
    checkServices();
    var package = acquirePackage(cache, wantedArchitecture);
    if (versionCompare(windowsVersion(), package.manifest.minimumWindows) < 0) { fail("此安装包要求 Windows " + package.manifest.minimumWindows + " 或更新版本。"); }
    if (package.manifest.dependencies.length) {
        fail("当前下载版本新增了外部依赖：" + package.manifest.dependencies.join(", ") + "。需要更新本工具后再部署，避免下载来源不明的组件。");
    }
    message("安装包清单未声明额外运行库依赖，无需另装 Python、Node.js、VC++ 或 WebView2。");
    var license = acquireLicense(cache);
    var previous = provisionedPackages();
    if (previous.code !== 0 && needsComponentRepair(previous)) {
        repairWindows("查询预配状态时报告 Windows 组件存储损坏");
    }
    if (previous.code !== 0) { displayFailure(previous); fail("无法查询现有预配状态，停止部署。"); }
    var installedVersion = findProvisionedVersion(previous.text, wantedArchitecture);
    var rebootRequired = false;
    if (!installedVersion || versionCompare(installedVersion, package.manifest.version) < 0) {
        message("开始部署。Windows 正在校验包签名并安装，请勿关闭窗口。");
        var installArguments = servicingArguments(["/Add-ProvisionedAppxPackage", "/PackagePath:" + package.path,
            "/LicensePath:" + license, "/Region:all"], "install");
        var deployed = runProgram(joinPath(systemDirectory, "dism.exe"), installArguments, "deploy-chatgpt");
        if (!successful(deployed.code) && needsComponentRepair(deployed)) {
            displayFailure(deployed);
            repairWindows("安装日志明确报告 Windows 组件存储损坏");
        }
        if (!successful(deployed.code)) { displayFailure(deployed); collectDeploymentEvents(); fail(describeInstallError(deployed)); }
        rebootRequired = deployed.code === 3010;
        var verified = provisionedPackages();
        installedVersion = findProvisionedVersion(verified.text, wantedArchitecture);
        if (verified.code !== 0 || !installedVersion || versionCompare(installedVersion, package.manifest.version) < 0) {
            collectDeploymentEvents();
            fail("部署命令已结束，但未核验到目标预配版本，因此不会报告成功。请查看日志。");
        }
    }
    message("PROVISIONED_SIGNIN_REQUIRED");
    message("已核验 ChatGPT 预配成功，版本 " + installedVersion + "。");
    if (rebootRequired) { message("Windows 要求重启。请先保存工作，手动重启云电脑，再从开始菜单打开 ChatGPT。"); }
    else { message("请保存工作，注销 Windows 并重新登录，再从开始菜单打开 ChatGPT。仅断开远程连接不等于注销。"); }
    message("预配成功不等于当前用户已启动成功；重新登录后打开应用，再从应用或 Microsoft Store 检查更新。");
    message("安装包和许可证已保留，方便重试；本工具不会自动注销或重启。");
    message("日志目录：" + logDirectory);
}

function usage() {
    message("ACE64 ChatGPT 离线下载与安装助手 v1.1.0（非 OpenAI 官方脚本）");
    message("优先官方安装器或 Microsoft Store；只有下载不成功时再使用本工具。");
    message("ChatGPT-Install.bat                下载并预配安装（安装需要管理员批准）");
    message("ChatGPT-Download.bat               仅下载，不安装，不请求管理员权限");
    message("--cache <目录>                     指定已有或新下载目录");
    message("--refresh                          重新从官方获取最新版，不复用旧包");
    message("--check                            本地只读检查，不下载或安装");
    message("--self-test                        纯逻辑自测，不下载或安装");
    message("--no-pause                         运行结束不等待按键，供自动化使用");
    message("离线包约 1 GB；下载仍需联网，版本大小与耗时以实际网络为准。");
}

function main() {
    for (var index = 0; index < WScript.Arguments.length; index++) {
        var argument = String(WScript.Arguments.Item(index));
        if (argument === "--self-test") { options.selfTest = true; }
        else if (argument === "--check") { options.check = true; }
        else if (argument === "--download-only") { options.downloadOnly = true; }
        else if (argument === "--refresh") { options.refresh = true; }
        else if (argument === "--no-pause") { options.noPause = true; }
        else if (argument === "--help") { options.noPause = true; usage(); return 0; }
        else if (argument === "--elevated") { options.elevated = true; }
        else if (argument === "--cache" && index + 1 < WScript.Arguments.length) { options.cache = String(WScript.Arguments.Item(++index)); }
        else { fail("未知参数：" + argument); }
    }
    if (options.selfTest) { selfTest(); return 0; }
    var downloads = registryValue("HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders\\{374DE290-123F-4565-9164-39C4925E467B}", "%USERPROFILE%\\Downloads");
    var cache = fileSystem.GetAbsolutePathName(options.cache || joinPath(commandShell.ExpandEnvironmentStrings(downloads), "ChatGPT-offline"));
    quote(cache);
    if (options.check) { checkOnly(cache); return 0; }
    var wantedArchitecture = architecture();
    if (options.downloadOnly) { return runDownload(cache, wantedArchitecture); }
    if (!isAdministrator()) {
        if (options.elevated) { fail("没有取得管理员权限。请右键本工具，选择“以管理员身份运行”。"); }
        message("即将请求 Windows 管理员授权，请在系统弹窗中选择“是”。");
        var shellApplication = new ActiveXObject("Shell.Application");
        shellApplication.ShellExecute(WScript.FullName,
            "//nologo //E:JScript " + quote(WScript.ScriptFullName) + " --elevated --cache " + quote(cache) + (options.refresh ? " --refresh" : "") + (options.noPause ? " --no-pause" : ""), "", "runas", 1);
        return 99;
    }
    runInstallation(cache, wantedArchitecture);
    return 0;
}

var exitCode = 1;
try { exitCode = main(); }
catch (error) {
    message("未完成：" + (error.message || error.description || String(error)));
    if (logDirectory) { message("诊断日志保留在：" + logDirectory); }
} finally {
    if (eventLog) { eventLog.Close(); }
    for (var index = 0; index < transientFiles.length; index++) {
        try { if (fileSystem.FileExists(transientFiles[index])) { fileSystem.DeleteFile(transientFiles[index], false); } }
        catch (error) { WScript.Echo("未能清理本次未完成下载：" + transientFiles[index]); }
    }
    if (ownedLock) {
        try { fileSystem.DeleteFile(ownedLock, false); }
        catch (error) { WScript.Echo("未能移除本工具创建的锁：" + ownedLock); }
    }
}
if (!options.check && !options.selfTest && !options.noPause && exitCode !== 99) {
    try { WScript.StdOut.Write("按回车键关闭窗口……"); WScript.StdIn.ReadLine(); } catch (error) {}
}
WScript.Quit(exitCode);
