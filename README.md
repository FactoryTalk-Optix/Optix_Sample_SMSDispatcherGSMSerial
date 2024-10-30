# Sample for manage COM GSM Modem and send SMS 

This project illustrates how to send SMS text messages with the activation of an alarm and also be able to select which threshold to send the message with. The project and netlogic are designed for GSM modems in serial with AT command support.

It's work for all platform: Windows, Ubuntu, OptixPanel. System must have a serial port.

## Setup
Drop the Netlogic into the Netlogic folder and configure the parameters:
- Link your *SerialPort* from CommDrivers to the alias **ModemSerialPort**
- Point to the folder containing all groups of users recipients to  **Recipents**
- Point to the folder containing all alarms to monitoring for sending SMS to  **AlarmsFolder**

## Notes
- A serial commdriver must be present into the project (1 Token)
- Alarms must be customized by adding boolean variables named:
    - SendSMSonON
    - SendSMSonOFF
    - SendSMSonACK
    - Only for Analog alarms
        - SendSMSonHighHigh
        - SendSMSonHigh
        - SendSMSonLow
        - SendSMSonLowLow
- Recipients should be a User customized with the addition of a string variable named **PhoneNumber** and add to a Group. The scripts check only Groups
- Manually send SMS is possible with the exposed method **SendSingleSMSMessage** 
- Manually send AT Command is possible with the exposed method **SendSingleATCommand** 

## Disclaimer

Rockwell Automation maintains these repositories as a convenience to you and other users. Although Rockwell Automation reserves the right at any time and for any reason to refuse access to edit or remove content from this Repository, you acknowledge and agree to accept sole responsibility and liability for any Repository content posted, transmitted, downloaded, or used by you. Rockwell Automation has no obligation to monitor or update Repository content

The examples provided are to be used as a reference for building your own application and should not be used in production as-is. It is recommended to adapt the example for the purpose, observing the highest safety standards.
