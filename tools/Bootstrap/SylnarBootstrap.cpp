#include <windows.h>
#include <shlobj.h>
#include <filesystem>
#include <string>
#include <vector>

namespace {

constexpr int kPayloadResource = 101;

std::filesystem::path LogDirectory()
{
    wchar_t* raw = nullptr;
    if (SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_CREATE, nullptr, &raw) != S_OK || !raw)
        return {};
    std::filesystem::path path(raw);
    CoTaskMemFree(raw);
    path /= L"Sylnar";
    std::error_code ec;
    std::filesystem::create_directories(path, ec);
    return path;
}

void Log(const std::string& message)
{
    const auto directory = LogDirectory();
    if (directory.empty()) return;
    const auto path = directory / L"bootstrap.log";
    HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    SYSTEMTIME now{};
    GetSystemTime(&now);
    char stamp[64];
    const int count = wsprintfA(stamp, "[%04u-%02u-%02u %02u:%02u:%02u.%03u] ",
        now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, now.wMilliseconds);
    DWORD written = 0;
    WriteFile(file, stamp, static_cast<DWORD>(count), &written, nullptr);
    WriteFile(file, message.c_str(), static_cast<DWORD>(message.size()), &written, nullptr);
    WriteFile(file, "\r\n", 2, &written, nullptr);
    CloseHandle(file);
}

void Log(const std::wstring& message)
{
    const int length = WideCharToMultiByte(CP_UTF8, 0, message.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (length <= 1) return;
    std::string utf8(static_cast<size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, message.c_str(), -1, utf8.data(), length, nullptr, nullptr);
    utf8.pop_back();
    Log(utf8);
}

std::wstring ErrorText(DWORD error)
{
    wchar_t* buffer = nullptr;
    const DWORD length = FormatMessageW(
        FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr, error, 0, reinterpret_cast<wchar_t*>(&buffer), 0, nullptr);
    std::wstring text = length && buffer ? std::wstring(buffer, length) : L"Windows error " + std::to_wstring(error);
    if (buffer) LocalFree(buffer);
    while (!text.empty() && (text.back() == L'\r' || text.back() == L'\n' || text.back() == L' '))
        text.pop_back();
    return text;
}

[[noreturn]] void Fail(const std::wstring& action, DWORD error = 0)
{
    const std::wstring detail = error ? action + L": " + ErrorText(error) : action;
    Log(L"startup failed: " + detail);
    MessageBoxW(nullptr, detail.c_str(), L"Sylnar could not start", MB_OK | MB_ICONERROR);
    ExitProcess(1);
}

std::filesystem::path CurrentExecutable()
{
    std::vector<wchar_t> buffer(512);
    for (;;) {
        const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (!length) Fail(L"Could not locate Sylnar.exe", GetLastError());
        if (length < buffer.size() - 1) return std::filesystem::path(buffer.data(), buffer.data() + length);
        buffer.resize(buffer.size() * 2);
    }
}

std::filesystem::path CreateRunDirectory()
{
    wchar_t* temp = nullptr;
    if (SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_CREATE, nullptr, &temp) != S_OK || !temp)
        Fail(L"Could not locate the application data folder");
    std::filesystem::path root(temp);
    CoTaskMemFree(temp);
    root /= L"Sylnar";
    root /= L"runtime";

    GUID guid{};
    if (FAILED(CoCreateGuid(&guid))) Fail(L"Could not create a private launch directory");
    wchar_t id[40]{};
    if (!StringFromGUID2(guid, id, static_cast<int>(std::size(id)))) Fail(L"Could not name the launch directory");
    const auto directory = root / (std::to_wstring(GetCurrentProcessId()) + L"-" + id);
    std::error_code ec;
    std::filesystem::create_directories(directory, ec);
    if (ec) Fail(L"Could not create the launch directory", static_cast<DWORD>(ec.value()));
    return directory;
}

void ExtractPayload(const std::filesystem::path& destination)
{
    HRSRC resource = FindResourceW(nullptr, MAKEINTRESOURCEW(kPayloadResource), RT_RCDATA);
    if (!resource) Fail(L"The application payload is missing", GetLastError());
    HGLOBAL loaded = LoadResource(nullptr, resource);
    if (!loaded) Fail(L"The application payload could not be read", GetLastError());
    const DWORD size = SizeofResource(nullptr, resource);
    const auto* bytes = static_cast<const BYTE*>(LockResource(loaded));
    if (!size || !bytes) Fail(L"The application payload is empty");

    HANDLE file = CreateFileW(destination.c_str(), GENERIC_WRITE, 0, nullptr,
        CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) Fail(L"Could not unpack Sylnar.exe", GetLastError());
    DWORD offset = 0;
    while (offset < size) {
        const DWORD chunk = (size - offset > 1024 * 1024) ? 1024 * 1024 : size - offset;
        DWORD written = 0;
        if (!WriteFile(file, bytes + offset, chunk, &written, nullptr) || written != chunk) {
            const DWORD error = GetLastError();
            CloseHandle(file);
            Fail(L"Could not finish unpacking Sylnar.exe", error);
        }
        offset += written;
    }
    if (!CloseHandle(file)) Fail(L"Could not close the unpacked application", GetLastError());
}

std::wstring ChildCommandLine(const std::filesystem::path& executable)
{
    const wchar_t* original = GetCommandLineW();
    if (*original == L'"') {
        ++original;
        while (*original && *original != L'"') ++original;
        if (*original == L'"') ++original;
    } else {
        while (*original && *original != L' ' && *original != L'\t') ++original;
    }
    return L"\"" + executable.native() + L"\"" + original;
}

} // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    const auto outer = CurrentExecutable();
    const auto runDirectory = CreateRunDirectory();
    const auto inner = runDirectory / L"Sylnar.exe";
    ExtractPayload(inner);
    Log("launch directory created");

    if (!SetEnvironmentVariableW(L"DOTNET_BUNDLE_EXTRACT_BASE_DIR", (runDirectory / L"native").c_str()))
        Fail(L"Could not configure the application runtime", GetLastError());
    if (!SetEnvironmentVariableW(L"SYLNAR_OUTER_EXE", outer.c_str()))
        Fail(L"Could not configure the application updater", GetLastError());

    auto commandLine = ChildCommandLine(inner);
    STARTUPINFOW startup{ sizeof(startup) };
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(inner.c_str(), commandLine.data(), nullptr, nullptr, TRUE, 0,
        nullptr, nullptr, &startup, &process))
        Fail(L"Could not start the application", GetLastError());

    CloseHandle(process.hThread);
    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD exitCode = 1;
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(process.hProcess);
    Log("application exited; cleaning launch directory");
    std::error_code ec;
    std::filesystem::remove_all(runDirectory, ec);
    return static_cast<int>(exitCode);
}
