# TCP Bridge UI Windows

This folder contains Unity Editor windows for managing the TCP Bridge server.

## Windows Overview

### TcpBridgeControlWindow

**Access**: `Tools > Codely Bridge > Control Window`

The main control panel for managing the TCP Bridge server. Features:

- **Server Status**: Real-time status display with visual indicators
- **Server Controls**: Start, Stop, and Restart buttons
- **Configuration**: Debug logging toggle
- **Port Management**: View current port, discover new ports, check availability
- **Debug Information**: System info and console access

### TcpBridgeStatusWindow

**Access**: `Tools > Codely Bridge > Status Window`

A compact status window that can be docked anywhere in Unity for quick monitoring:

- **Status Indicator**: Running/Stopped with port information
- **Quick Controls**: Start/Stop buttons
- **Control Window Access**: Quick link to full control window

### TcpBridgeMenuItems

**Access**: `Tools > Codely Bridge > [various options]`

Menu items for quick operations:

- **Start Server** - Start the TCP bridge
- **Stop Server** - Stop the TCP bridge
- **Restart Server** - Restart the TCP bridge
- **Show Server Status** - Display status dialog
- **Discover New Port** - Find and optionally switch to new port
- **Enable/Disable Debug Logging** - Toggle debug output

## Usage Tips

1. **First Time Setup**: The TCP bridge starts automatically when Unity loads
2. **Status Monitoring**: Keep the Status Window docked for quick monitoring
3. **Port Issues**: Use "Discover New Port" if you encounter port conflicts
4. **Debugging**: Enable debug logging when troubleshooting connection issues
5. **Menu Access**: Use keyboard shortcuts or menu items for quick operations

## Integration

All windows are designed to work together:

- Status Window → Control Window (full features)
- Menu Items → Quick operations without opening windows
- Control Window → Comprehensive management interface

The UI automatically updates to reflect the current server state and provides consistent feedback across all interfaces.
