# **TCode Protocol Reference (v0.3)**

## **What is TCode?**

**Movement, Not Time.**

Unlike standard *Timecode* (which tracks a specific position in an audio or video file), **TCode** is a hardware-control protocol. It is specifically designed to tell a microcontroller exactly where to move a physical motor and how long that movement should take.

Commands are sent via plain-text strings, usually over UDP or serial connections, allowing applications (like ToySerialController) to interface seamlessly with physical hardware.

## **Command Syntax Breakdown**

A standard TCode command looks like this: L09999I2000

It can be broken down into four distinct parts:

1. **Axis Type (L)**: The type of movement (Linear, Rotation, Vibration, etc.).  
2. **Channel (0)**: The specific motor/channel being targeted (0-9).  
3. **Magnitude (9999)**: The target position or intensity, expressed as a 4-digit number from 0000 to 9999\.  
   * 0000 \= Minimum position  
   * 5000 \= Center/Neutral position  
   * 9999 \= Maximum position  
4. **Extension / Interval (I2000)**: Optional. Instructs the hardware on the timing of the movement.  
   * I\[ms\] \= Interval. Execute the movement over the specified milliseconds (e.g., I2000 means take 2 seconds to reach the target).  
   * *If no interval is provided, the hardware attempts to execute the command instantly.*

**Example:** L09999I2000 means *"Move the main linear axis (Stroke) to the maximum position (9999) smoothly over 2000 milliseconds."*

## **Hardware Axis Dictionary**

Standardized mappings ensure that software sends the right commands to the right physical components.

### **↕️ Linear Axes (L)**

| TCode | Movement Type | Common Hardware Mapping |
| :---- | :---- | :---- |
| **L0** | Up / Down | Main Stroke |
| **L1** | Forward / Back | Surge |
| **L2** | Left / Right | Sway |

### **⟳ Rotation Axes (R)**

| TCode | Movement Type | Common Hardware Mapping |
| :---- | :---- | :---- |
| **R0** | Twist | Spin |
| **R1** | Roll | Tilt-X |
| **R2** | Pitch | Tilt-Y |

### **⚙ Auxiliary & Vibration (A / V)**

| TCode | Movement Type | Common Hardware Mapping |
| :---- | :---- | :---- |
| **V0** | Vibration | Main Vibration Motor |
| **A0** | Suction | Vacuum / Valve |
| **A1** | Lube | Pump / Dispenser |

## **Multiple Commands**

You can send multiple commands in a single payload by separating them with a space or a newline character (\\n).

**Example Payload:**

L05000I500 R15000I500 V09999

*Action: Return Stroke to center over 500ms, return Roll to center over 500ms, and turn Vibration up to maximum instantly.*