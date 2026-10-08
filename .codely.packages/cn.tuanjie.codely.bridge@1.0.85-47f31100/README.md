# Codely Bridge

Codely Bridge is a lightweight TCP socket server implementation for Unity Editor that provides networking capabilities with port management and client handling.

## Features

### 🚀 TCP Socket Server

- **Modern Architecture**: Built with async/await patterns for Unity Editor
- **Automatic Port Management**: Smart port allocation with project-specific persistence
- **Client Connection Handling**: Multi-client support with proper connection management
- **Socket Configuration**: Optimized socket options for reliable connections

### ⚙️ Port Management

- **Dynamic Port Allocation**: Automatically finds available ports starting from default (25916)
- **Project-Specific Storage**: Remembers port settings per Unity project
- **Port Conflict Resolution**: Handles port conflicts gracefully with fallback options
- **Cross-Platform Support**: Works on Windows, macOS, and Linux

### 🔧 TCP Features

- **Keep-Alive Support**: Configurable socket keep-alive for stable connections
- **Connection Timeout**: Prevents hanging connections with configurable timeouts
- **Multi-Client Support**: Handles multiple simultaneous client connections
- **Graceful Shutdown**: Proper cleanup on Unity Editor shutdown or assembly reload

### 🎛️ Unity Editor Integration

- **Control Window**: Full-featured TCP bridge management interface (`Tools > Codely Bridge > Control Window`)
- **Status Window**: Compact dockable status monitor (`Tools > Codely Bridge > Status Window`)
- **Menu Items**: Quick actions via Unity menu (`Tools > Codely Bridge`)
- **Real-time Status**: Live server status updates and port information
- **Debug Controls**: Toggle debug logging and port management tools

## Installation

### Requirements

- Unity 2021.3 or later

### Setup

1. Add this package to your Unity project
2. The TCP server will start automatically when Unity loads
3. Check Unity Console for server startup confirmation and port information

## Usage

### Basic TCP Server

The TCP server starts automatically when Unity loads and listens for client connections. By default:

- **Default Port**: 25916 (automatically managed)
- **Protocol**: Basic TCP with welcome message
- **Echo Server**: Currently configured as an echo server (sends received data back to client)

### Using the Unity Editor UI

Access the TCP Bridge controls through Unity's menu system:

1. **Control Window**: `Tools > Codely Bridge > Control Window`
   - Complete server management interface
   - Real-time status monitoring
   - Server start/stop/restart controls
   - Port management and configuration
   - Debug logging controls

2. **Status Window**: `Tools > Codely Bridge > Status Window`
   - Compact status display (can be docked)
   - Quick start/stop buttons
   - Shows current port and connection status

3. **Menu Actions**: `Tools > Codely Bridge > [Action]`
   - Quick server operations
   - Port discovery and management
   - Debug logging toggle

### Connecting to the Server

```bash
# Example using telnet (replace 25916 with actual port shown in Unity Console or UI)
telnet localhost 25916
```

### Customizing the Protocol

The current implementation includes a basic echo server. To customize for your needs:

1. Modify the `HandleClientAsync` method in `UnityTcpBridge.cs`
2. Replace the echo logic with your custom protocol handling
3. Update the welcome message format if needed

### Port Management

```csharp
// Get the current port
int port = UnityTcpBridge.GetCurrentPort();

// Check if server is running
bool isRunning = UnityTcpBridge.IsRunning;

// Manually start/stop (usually automatic)
UnityTcpBridge.Start();
UnityTcpBridge.Stop();
```

## Architecture

### TCP Server Implementation

```
External TCP Client → Codely Bridge Server → Custom Protocol Handler
```

### Key Components

- **UnityTcpBridge**: Main TCP server with lifecycle management
- **PortManager**: Dynamic port allocation and persistence
- **TcpLog**: Logging utility for TCP operations

### Performance Features

- **Async Operations**: Non-blocking I/O operations
- **Connection Pooling**: Efficient client connection management
- **Resource Cleanup**: Automatic cleanup on Unity lifecycle events

## Configuration

### Debug Logging

Enable detailed logging for troubleshooting:

```csharp
// In Unity Editor, set this EditorPref:
EditorPrefs.SetBool("UnityTcp.DebugLogs", true);
```

### Port Configuration

The server automatically manages ports, but you can access port management:

```csharp
// Check if a port is available
bool available = PortManager.IsPortAvailable(25916);

// Get port with fallback
int port = PortManager.GetPortWithFallback();

// Discover a new port
int newPort = PortManager.DiscoverNewPort();
```

## Development

### Git Hooks Setup

This project uses Git hooks to validate `.meta` file GUIDs before commit. After cloning, run once:

```bash
git config core.hooksPath .githooks
```

After this, `git commit` will automatically validate that all staged `.meta` files contain valid 32-character lowercase hexadecimal GUIDs.

### Building from Source

#### Option A — Inside Unity (standard workflow)

1. Clone this repository
2. Open in Unity 2021.3 or later as a package (via `Packages/manifest.json` or Package Manager)
3. Unity compiles the assembly automatically from `Editor/UnityTcp.Editor.asmdef`

#### Option B — CLI release build (`scripts/pack-release.ps1`)

Compiles `Editor/UnityTcp.Editor.csproj` with `dotnet build` outside of Unity and assembles a `release/` directory ready for distribution.

**Prerequisites**

- [.NET SDK](https://dotnet.microsoft.com/download) (any recent version)
- Unity 2021.3 or later installed (needed for `UnityEngine.dll` / `UnityEditor.dll` references)

**Usage**

```powershell
# Auto-detect Unity at the default Hub path
.\scripts\pack-release.ps1

# Specify a Unity Managed path explicitly
.\scripts\pack-release.ps1 -UnityManagedPath "C:\Program Files\Unity\Hub\Editor\2021.3.45f1\Editor\Data\Managed"

# Also create a .tgz archive of the release directory
.\scripts\pack-release.ps1 -PackTgz

# Using an environment variable instead of the parameter
$env:UNITY_MANAGED_PATH = "C:\Program Files\Unity\Hub\Editor\2021.3.45f1\Editor\Data\Managed"
.\scripts\pack-release.ps1
```

**Output layout**

```
release/
├── package.json
├── Editor/
│   ├── UnityTcp.Editor.dll   ← compiled assembly
│   ├── fonts/
│   └── Icons/
└── bin/                      ← copied from package root bin/ (if present)
```

The intermediate build artefacts are written to `build/` (not committed).

### Customizing the Protocol

To implement your custom TCP protocol:

1. **Modify HandleClientAsync**: Replace the echo server logic
2. **Update Welcome Message**: Change the initial handshake
3. **Add Message Parsing**: Implement your message format
4. **Error Handling**: Add appropriate error handling for your protocol

Example customization:

```csharp
// In HandleClientAsync method, replace echo logic with:
while (!token.IsCancellationRequested && client.Connected)
{
    // Read client message
    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);
    if (bytesRead == 0) break;

    // Parse your custom message format
    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

    // Process the message according to your protocol
    string response = ProcessCustomMessage(message);

    // Send response
    byte[] responseBytes = Encoding.UTF8.GetBytes(response);
    await stream.WriteAsync(responseBytes, 0, responseBytes.Length, token);
}
```

## Troubleshooting

### Common Issues

**Port Already in Use**

- The server will automatically find an alternative port
- Check Unity Console for the actual port being used
- Use `PortManager.DiscoverNewPort()` to force a new port

**Server Won't Start**

- Verify Unity project is loaded properly
- Check Unity Console for error messages
- Ensure no firewall is blocking the port

**Client Can't Connect**

- Verify the server is running (check Unity Console)
- Use the correct port number shown in Unity Console
- Ensure localhost connectivity

**Connection Drops**

- Check if client is sending data within timeout period (60 seconds default)
- Enable debug logging to see connection details
- Verify client is properly handling the welcome message

### Debug Logging

Enable detailed logging to get comprehensive information about:

- Server startup and port allocation
- Client connections and disconnections
- Data transmission and errors
- Port management decisions

## CDN releases and main snapshots

See the [发布指南](docs/发布指南.md) for instructions on preparing,
validating, publishing, and recovering stable CDN releases, and for automatic
per-commit CDN snapshots of new commits entering `main`.

## License

This project is licensed under the MIT License.
