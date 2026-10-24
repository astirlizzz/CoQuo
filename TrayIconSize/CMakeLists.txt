cmake_minimum_required(VERSION 3.24)
project(TrayIconSize LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_compile_options(/W4 /permissive- /utf-8 /EHsc)
add_compile_definitions(UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)

# Loaded into explorer.exe by XAML Diagnostics.
add_library(TrayIconSizeHook SHARED
    src/Hook/Hook.cpp
    src/Hook/Hook.def)
target_include_directories(TrayIconSizeHook PRIVATE src/Common)
target_link_libraries(TrayIconSizeHook PRIVATE windowsapp ole32 oleaut32 shell32)

# One per user session; started on demand by the client.
add_executable(TrayIconSizeHost WIN32
    src/Host/Host.cpp)
target_include_directories(TrayIconSizeHost PRIVATE src/Common)

# The SDK applications load.
add_library(TrayIconSizeClient SHARED
    src/Client/Client.cpp)
target_include_directories(TrayIconSizeClient PUBLIC include PRIVATE src/Common)
