cmake_minimum_required(VERSION 3.20)
project(NativeBrowser CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)


# Statically link the MSVC runtime so users' VC++ Redistributable version
# does not affect us. An outdated MSVCP140.dll on the host (e.g. VS 2017's
# 14.11.x) ships an older _Mtx_internal_imp_t layout than the one this DLL
# was compiled against, and std::mutex::lock SIGSEGVs on first call.
# Requires CMP0091 NEW (default since CMake 3.15).
if(MSVC)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()
# ── WebView2 SDK ──────────────────────────────────────────────────────────────
set(WEBVIEW2_DIR "${CMAKE_SOURCE_DIR}/vendor/webview2")

if(NOT EXISTS "${WEBVIEW2_DIR}/include/WebView2.h")
    message(FATAL_ERROR "WebView2 SDK not found at ${WEBVIEW2_DIR}. "
        "Run: nuget install Microsoft.Web.WebView2 -OutputDirectory vendor/nuget "
        "and copy include/ and lib/x64/ to vendor/webview2/")
endif()

# ── 目标 DLL ─────────────────────────────────────────────────────────────────
add_library(NativeBrowser SHARED
    src/NativeBrowser.cpp
    src/BrowserInstance.cpp
    src/STAThread.cpp
    src/WndProcHook.cpp
)

target_include_directories(NativeBrowser PRIVATE
    include/
    ${WEBVIEW2_DIR}/include/
)

target_compile_definitions(NativeBrowser PRIVATE
    NATIVEBROWSER_EXPORTS
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

target_compile_options(NativeBrowser PRIVATE
    /W3
    /WX-       # 不把警告当错误（方便迭代）
    /MP        # 并行编译
)

target_link_libraries(NativeBrowser PRIVATE
    ${WEBVIEW2_DIR}/lib/x64/WebView2LoaderStatic.lib
    shlwapi.lib        # WebView2Loader 依赖
    version.lib        # WebView2Loader 依赖
    comctl32.lib       # SetWindowSubclass
    ole32.lib          # CoInitializeEx
    oleaut32.lib
)

# ── 构建后复制到 Plugins/Win/ 目录
set(PLUGIN_OUTPUT_DIR "${CMAKE_SOURCE_DIR}/..")
add_custom_command(TARGET NativeBrowser POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${PLUGIN_OUTPUT_DIR}"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:NativeBrowser>"
        "${PLUGIN_OUTPUT_DIR}/NativeBrowser.dll"
    COMMENT "Copying NativeBrowser.dll → Plugins/Win/"
)
