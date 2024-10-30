#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.Retentivity;
using FTOptix.NativeUI;
using FTOptix.UI;
using FTOptix.Core;
using FTOptix.CoreBase;
using FTOptix.NetLogic;
using FTOptix.EventLogger;
using FTOptix.Store;
using FTOptix.Alarm;
using FTOptix.SerialPort;
using FTOptix.CommunicationDriver;
using System.Net.Mail;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using FTOptix.Recipe;
using FTOptix.DataLogger;
using FTOptix.WebUI;
#endregion

public class AlarmSMSDispatcherLogic : BaseNetLogic
{  
    public override void Start()
    {
        alarmsFolder = LogicObject.GetAlias("AlarmsFolder") as Folder;
        modemSerialPort = LogicObject.GetAlias("ModemSerialPort") as SerialPort;
        recipent = LogicObject.GetAlias("Recipents") as Group;
        simPin = LogicObject.GetVariable("SIM PIN");
        pinStatus = LogicObject.GetVariable("PIN Status");
        initCommand = LogicObject.GetVariable("Init command");
        registrationStatus = LogicObject.GetVariable("Registration status");
        actualCarrier = LogicObject.GetVariable("Actual Carrier");
        signalStrange = LogicObject.GetVariable("Signal Strange");
        numberofRetry = LogicObject.GetVariable("Number of retry");
        autoRefreshInfo = LogicObject.GetVariable("AutoRefreshInfo");
        autoUnlockPIN = LogicObject.GetVariable("AutoUnlockPIN");


        pinStatus.Value = (byte)pinStatusEnum.Unknown;
        modemSendMessage = new LongRunningTask(SendMessage, LogicObject);
        modemQueueWorker = new PeriodicTask(ManageMessageQueue, 3000, LogicObject);
        alarmObserver = new PeriodicTask(CheckNewAlarms, 200, LogicObject);
        modemStatusCheckTask = new PeriodicTask(StatusModemCheck, 15000, LogicObject);
        memoryAlarmsNodeList = new List<IUANode>();
        messagesToSend = new List<messageQueue>();
        memoryAlarmsList = new List<memoryAlarm>();
        manualUpdateInfoRequest = true;        
        modemStatusCheckTask.Start();
        modemQueueWorker.Start();
        alarmObserver.Start();        
    }

    public override void Stop()
    {
        modemQueueWorker?.Cancel();        
        modemStatusCheckTask?.Cancel();
        alarmObserver?.Cancel();
    }

    [ExportMethod]
    public void UpdateModemStatus()
    {
        manualUpdateInfoRequest = true;
        modemStatusCheckTask.Cancel();
        modemStatusCheckTask.Start();
    }

    [ExportMethod]
    public void SendSingleATCommand(string command,NodeId variableOutput)
    {
        IUAVariable recive = InformationModel.GetVariable(variableOutput);
        recive.Value = "";
        recive.Value = SendATCommand(command+"\r");
    }

    [ExportMethod]
    public void SendSingleSMSMessage(string DestinationNumber, string Message)
    {
        destinationNumber = DestinationNumber;
        message = Message;
        modemSendMessage.Start();
    }

    [ExportMethod]
    public void UnlockPINSim()
    {
        UnlockSIM();
    }

    private void StatusModemCheck()
    {      
        try
        {
            if (autoRefreshInfo.Value || manualUpdateInfoRequest)
            {
                if (!runningSendingMessages)
                {
                    runningGetModemStatus = true;
                    manualUpdateInfoRequest = false;
                    if (!string.IsNullOrEmpty(initCommand.Value)) SendATCommand($"{initCommand.Value.Value.ToString().Trim()}\r");
                    string recivedElement = SendATCommand("AT+CPIN?\r").Split("\r").Where(x => x.Contains("+CPIN:")).FirstOrDefault();
                    if (!string.IsNullOrEmpty(recivedElement))
                    {
                        switch (recivedElement.Replace("+CPIN:", string.Empty).Trim())
                        {
                            case "READY":
                                pinStatus.Value = (byte)pinStatusEnum.Unlocked;
                                break;
                            case "SIM PIN":
                                pinStatus.Value = (byte)pinStatusEnum.Pin1Required;
                                if (autoUnlockPIN.Value && !pinErrorMemory)
                                    UnlockSIM();
                                break;
                            case "SIM PIN2":
                                pinStatus.Value = (byte)pinStatusEnum.Pin2Required;
                                break;
                            case "SIM PUK":
                                pinStatus.Value = (byte)pinStatusEnum.PukRequired;
                                break;
                            case "SIM PUK2":
                                pinStatus.Value = (byte)pinStatusEnum.Puk2Required;
                                break;
                            default:
                                pinStatus.Value = (byte)pinStatusEnum.Unknown;
                                break;
                        }
                    }
                    else
                        pinStatus.Value = (byte)pinStatusEnum.Unknown;

                    recivedElement = SendATCommand("AT+CREG?\r").Split("\r").Where(x => x.Contains("+CREG:")).FirstOrDefault();
                    if (!string.IsNullOrEmpty(recivedElement))
                    {
                        int registrationStatusparsed = 99;
                        int.TryParse(recivedElement.Trim().Substring(recivedElement.Trim().Length - 1), out registrationStatusparsed);
                        registrationStatus.Value = registrationStatusparsed;
                    }
                    else
                        registrationStatus.Value = 99;
                    recivedElement = SendATCommand("AT+COPS?\r").Split("\r").Where(x => x.Contains("+COPS:")).FirstOrDefault();
                    if (!string.IsNullOrEmpty(recivedElement))
                    {
                        int commaPosition = recivedElement.Trim().LastIndexOf(',');
                        actualCarrier.Value = commaPosition > -1 ? recivedElement.Trim().Substring(commaPosition + 1) : "No signal";

                    }
                    else
                        actualCarrier.Value = string.Empty;
                    recivedElement = SendATCommand("AT+CSQ\r").Split("\r").Where(x => x.Contains("+CSQ:")).FirstOrDefault();
                    if (!string.IsNullOrEmpty(recivedElement))
                    {
                        int signalStrangeParsed = 99;
                        int.TryParse(recivedElement.Replace("+CSQ:", string.Empty).Trim().Split(',')[0], out signalStrangeParsed);
                        signalStrange.Value = signalStrangeParsed;
                    }
                }
            }
        }
        catch (Exception ex) 
        {
            Log.Error(LogicObject.BrowseName, ex.Message);
        }
        finally 
        {
            runningGetModemStatus = false;
        }
    }

    private void UnlockSIM()
    {
        string recivedElement = SendATCommand($"AT+CPIN=\"{simPin.Value.ToString().Trim()}\"\r");
        if (recivedElement.ToUpper().Contains("OK"))
            Log.Info(LogicObject.BrowseName, "SIM Unlocked correctly");
        else
        {
            pinErrorMemory = true;
            Log.Warning(LogicObject.BrowseName, "Wrong PIN, sim locked");
        }
    }

    private void CheckNewAlarms()
    {
        try
        {
            if (alarmsNode == null && Project.Current.Owner.Find("RetainedAlarms") == null)
                return;
            else
            {
                if (Project.Current.Owner.Find("RetainedAlarms") != null && alarmsNode == null)
                    alarmsNode = Project.Current.Owner.Find("RetainedAlarms").Children.First(x => x.NodeClass == NodeClass.Object);
            }
            List<IUANode> newAlarms = alarmsNode.Children.Except(memoryAlarmsNodeList).ToList();
            List<IUANode> oldAlarms = memoryAlarmsNodeList.Except(alarmsNode.Children).ToList();
            foreach (IUANode alarm in alarmsNode.Children.Intersect(memoryAlarmsNodeList).ToList())
            {
                IUANode alarmNode = alarmsFolder.Find(alarm.BrowseName);
                bool ack = alarm.GetVariable("AckedState")?.GetVariable("Id")?.Value;
                bool confirmed = alarm.GetVariable("ConfirmedState")?.GetVariable("Id")?.Value;
                memoryAlarm alarmMemory = memoryAlarmsList.Where(x => x.alarmNode.NodeId == alarm.NodeId).First();
                if (alarmMemory != null && alarmMemory.alarmAckState != ack)
                {
                    if (ack && alarmMemory.sendACK) messagesToSend.Add(new messageQueue { alarmText = alarmMemory.alarmText, messageRegistrationDate = DateTime.Now, alarmStatus = "Alarm ACK", retryCount = 0 });
                    memoryAlarmsList.Where(x => x.alarmNode.NodeId == alarm.NodeId).ToList().ForEach(y => y.alarmAckState = ack);
                }
                if (alarmMemory != null && ack && alarmMemory.alarmConfirmedState != confirmed)
                {
                    memoryAlarmsList.Where(x => x.alarmNode.NodeId == alarm.NodeId).ToList().ForEach(y => y.alarmConfirmedState = confirmed);
                }
                if (alarmNode.GetType().IsAssignableTo(typeof(LimitAlarmController)))
                {
                    double inputValue = (alarmNode as LimitAlarmController).InputValue;
                    double? highLimit = (alarmNode as LimitAlarmController).HighLimit;
                    double? highHighLimit = (alarmNode as LimitAlarmController).HighHighLimit;
                    double? lowLimit = (alarmNode as LimitAlarmController).LowLimit;
                    double? lowLowLimit = (alarmNode as LimitAlarmController).LowLowLimit;
                    bool alarmHighState = highLimit != null && inputValue >= highLimit;
                    bool alarmLowState = lowLimit != null && inputValue <= lowLimit;
                    bool alarmHighHighState = highHighLimit != null && inputValue >= highHighLimit;
                    bool alarmLowLowState = lowLowLimit != null && inputValue <= lowLowLimit;
                    if (alarmHighHighState && !alarmMemory.alarmHighHighState && alarmMemory.sendHighHigh)
                    {
                        messagesToSend.Add(new messageQueue { alarmText = alarmMemory.alarmText, messageRegistrationDate = DateTime.Now, alarmStatus = $"Alarm ACK on Level HIGH HIGH (Value {inputValue} >= {highHighLimit})", retryCount = 0 });
                        memoryAlarmsList.Where(x => x.alarmNode.NodeId == alarm.NodeId).ToList().ForEach(y => y.alarmHighHighState = true);
                    }
                    else
                        if (alarmHighState && !alarmMemory.alarmHighState && alarmMemory.sendHigh)
                    {
                        messagesToSend.Add(new messageQueue { alarmText = alarmMemory.alarmText, messageRegistrationDate = DateTime.Now, alarmStatus = $"Alarm ACK on Level HIGH (Value {inputValue} <= {highLimit})", retryCount = 0 });
                        memoryAlarmsList.Where(x => x.alarmNode.NodeId == alarm.NodeId).ToList().ForEach(y => y.alarmHighState = true);
                    }
                    if (alarmLowLowState && !alarmMemory.alarmLowLowState && alarmMemory.sendLowLow)
                    {
                        messagesToSend.Add(new messageQueue { alarmText = alarmMemory.alarmText, messageRegistrationDate = DateTime.Now, alarmStatus = $"Alarm ACK on Level LOW LOW (Value {inputValue} <= {lowLowLimit})", retryCount = 0 });
                        memoryAlarmsList.Where(x => x.alarmNode.NodeId == alarm.NodeId).ToList().ForEach(y => y.alarmLowLowState = true);
                    }
                    else
                        if (alarmLowState && !alarmMemory.alarmLowState && alarmMemory.sendLow)
                    { 
                        messagesToSend.Add(new messageQueue { alarmText = alarmMemory.alarmText, messageRegistrationDate = DateTime.Now, alarmStatus = $"Alarm ACK on Level LOW (Value {inputValue} <= {lowLimit})", retryCount = 0 });
                        memoryAlarmsList.Where(x => x.alarmNode.NodeId == alarm.NodeId).ToList().ForEach(y => y.alarmLowState = true);
                    }
                }
            }
            foreach (IUANode alarm in newAlarms)
            {
                memoryAlarmsNodeList.Add(alarm);
                IUANode alarmNode = alarmsFolder.Find(alarm.BrowseName);
                bool ack = alarm.GetVariable("AckedState")?.GetVariable("Id")?.Value;
                bool sendON = alarm.GetVariable("SendSMSonON") != null && alarm.GetVariable("SendSMSonON").Value == true;
                bool sendOFF = alarm.GetVariable("SendSMSonOFF") != null && alarm.GetVariable("SendSMSonOFF").Value == true;
                bool sendACK = alarm.GetVariable("SendSMSonACK") != null && alarm.GetVariable("SendSMSonACK").Value == true;
                bool sendHighHigh = alarm.GetVariable("SendSMSonHighHigh") != null && alarm.GetVariable("SendSMSonHighHigh").Value == true;
                bool sendHigh = alarm.GetVariable("SendSMSonHigh") != null && alarm.GetVariable("SendSMSonHigh").Value == true;
                bool sendLowLow = alarm.GetVariable("SendSMSonLowLow") != null && alarm.GetVariable("SendSMSonLowLow").Value == true;
                bool sendLow = alarm.GetVariable("SendSMSonLow") != null && alarm.GetVariable("SendSMSonLow").Value == true;
                bool alarmHighState = false;
                bool alarmLowState = false;
                bool alarmHighHighState = false;
                bool alarmLowLowState = false;
                LocalizedText localizedMessage = new LocalizedText("");
                if (alarmNode != null && alarmNode.GetVariable("Message")?.Value != null) localizedMessage = alarmNode.GetVariable("Message").Value;
                if (alarmNode.GetType().IsAssignableTo(typeof(LimitAlarmController)))
                {
                    double inputValue = (alarmNode as LimitAlarmController).InputValue;
                    double? highLimit = (alarmNode as LimitAlarmController).HighLimit;
                    double? highHighLimit = (alarmNode as LimitAlarmController).HighHighLimit;
                    double? lowLimit = (alarmNode as LimitAlarmController).LowLimit;
                    double? lowLowLimit = (alarmNode as LimitAlarmController).LowLowLimit;
                    alarmHighState = highLimit != null && inputValue >= highLimit;
                    alarmLowState = lowLimit != null && inputValue <= lowLimit;
                    alarmHighHighState = highHighLimit != null && inputValue >= highHighLimit;
                    alarmLowLowState = lowLowLimit != null && inputValue <= lowLowLimit;
                    if (alarmHighHighState && sendHighHigh)
                        messagesToSend.Add(new messageQueue { alarmText = localizedMessage, messageRegistrationDate = DateTime.Now, alarmStatus = $"Alarm ON on Level HIGH HIGH (Value {inputValue} >= {highHighLimit})", retryCount = 0 });
                    else
                        if (alarmHighState && sendHigh)
                        messagesToSend.Add(new messageQueue { alarmText = localizedMessage, messageRegistrationDate = DateTime.Now, alarmStatus = $"Alarm ON on Level HIGH (Value {inputValue} <= {highLimit})", retryCount = 0 });
                    if (alarmLowLowState && sendLowLow)
                        messagesToSend.Add(new messageQueue { alarmText = localizedMessage, messageRegistrationDate = DateTime.Now, alarmStatus = $"Alarm ON on Level LOW LOW (Value {inputValue} <= {lowLowLimit})", retryCount = 0 });
                    else
                        if (alarmLowState && sendLow)
                        messagesToSend.Add(new messageQueue { alarmText = localizedMessage, messageRegistrationDate = DateTime.Now, alarmStatus = $"Alarm ON on Level LOW (Value {inputValue} <= {lowLimit})", retryCount = 0 });
                }
                else
                    if (sendON && localizedMessage != null)
                    messagesToSend.Add(new messageQueue { alarmText = localizedMessage, messageRegistrationDate = DateTime.Now, alarmStatus = "Alarm ON", retryCount = 0 });

                memoryAlarmsList.Add(new memoryAlarm
                {
                    alarmNode = alarm,
                    alarmText = localizedMessage,
                    sendON = sendON,
                    sendOFF = sendOFF,
                    sendACK = sendACK,
                    sendHigh = sendHigh,
                    sendHighHigh = sendHighHigh,
                    sendLow = sendLow,
                    sendLowLow = sendLowLow,
                    alarmAckState = ack,
                    alarmConfirmedState = false,
                    alarmHighHighState = alarmHighHighState,
                    alarmHighState = alarmHighState,
                    alarmLowState = alarmLowState,
                    alarmLowLowState = alarmLowLowState
                });
            }
            foreach (IUANode alarm in oldAlarms)
            {
                memoryAlarm memoryAlarm = memoryAlarmsList.Where(x => x.alarmNode.NodeId == alarm.NodeId).First();
                if (memoryAlarm.sendOFF && memoryAlarm.alarmText != null) 
                    messagesToSend.Add(new messageQueue { alarmText = memoryAlarm.alarmText, messageRegistrationDate = DateTime.Now, alarmStatus = "Alarm OFF", retryCount = 0 });
                memoryAlarmsNodeList.Remove(alarm);
                memoryAlarmsList.Remove(memoryAlarm);
            }
        }
        catch(Exception ex)
        {
            Log.Error(LogicObject.BrowseName, ex.Message);
        }
    }

    private void ManageMessageQueue()
    {        
        if (messagesToSend != null && messagesToSend.Count > 0) 
        {
            try
            {
                if (!runningGetModemStatus && !runningSendingMessages)
                {
                    int retryMax = 2;
                    if (numberofRetry.Value != null && numberofRetry.Value > 0) retryMax = numberofRetry.Value -1;
                    runningSendingMessages = true;
                    List<messageQueue> messaggesToRemove = new List<messageQueue>();
                    foreach (messageQueue messageToSend in messagesToSend)
                    {
                        bool sendingFailed = false;
                        foreach (IUANode user in recipent.InverseRefs.GetObjects(FTOptix.Core.ReferenceTypes.HasGroup))
                        {
                            string phoneNumber = user.GetVariable("PhoneNumber")?.Value;
                            List<string> localeIds = new List<string>(new string[] { (user as User).LocaleId });
                            string messageSend = InformationModel.LookupTranslation(messageToSend.alarmText, localeIds).Text;
                            messageSend = string.IsNullOrEmpty(messageSend) ? messageToSend.alarmText.Text : messageSend;
                            if (!string.IsNullOrEmpty(phoneNumber) && !string.IsNullOrEmpty(messageSend))
                            {
                                if (!sendingFailed && messageToSend.retryCount > retryMax)
                                    messaggesToRemove.Add(messageToSend);
                                else if (SendSMS(phoneNumber, $"{messageToSend.messageRegistrationDate.ToString("g")} - {messageToSend.alarmStatus} - {messageSend}"))
                                {
                                    Log.Info(LogicObject.BrowseName, $"Messagge {messageSend} sent correctly");
                                    messaggesToRemove.Add(messageToSend);
                                }
                                else
                                    sendingFailed = true;
                            }
                            else
                                messaggesToRemove.Add(messageToSend);
                        }
                        if (sendingFailed) 
                            messagesToSend.Where(x => x == messageToSend).ToList().ForEach(y => y.retryCount++);
                    }
                    messagesToSend = messagesToSend.Except(messaggesToRemove).ToList();
                }
                
            }
            catch (Exception ex)
            {
                Log.Error(LogicObject.BrowseName, ex.Message);
            }
            finally
            {
                runningSendingMessages = false;
            }  
        }
    }

    private bool SendSMS(string phoneNumber, string message)
    {
        string recive;
        bool waitTextMessage = false;
        try
        {            
            recive = SendATCommand("AT+CMGF=1\r");
            if (!recive.ToUpper().Contains("OK")) 
                throw new InvalidOperationException("reply from modem is invalid for the command requested");
            recive = SendATCommand($"AT+CMGS=\"{phoneNumber}\"\r", reciveCheckCustom:">");
            if (!recive.Contains(">")) 
                throw new InvalidOperationException("reply from modem is invalid for the command requested");
            waitTextMessage = true;
            if (message.Length > 160) 
                message = message.Remove(159);            
            recive = SendATCommand(message + "\x1A");
            if (recive.ToUpper().Contains("OK"))
                Log.Debug(LogicObject.BrowseName, $"Message {message} sent");
            else
            {
                SendATCommand("\x1B"); // send esc for hangout CMGS
                Log.Debug(LogicObject.BrowseName, $"Error during sending message {message}");
            }
            return recive.ToUpper().Contains("OK");
        }
        catch ( Exception ex ) 
        {
            if (waitTextMessage) 
                SendATCommand("\x1B"); // send esc for hangout CMGS
            Log.Error(LogicObject.BrowseName, $"Error during sending SMS {message}: {ex.Message}");
            return false;
        }     
    }

    private string SendATCommand(string command, string reciveCheckCustom = "")
    {
        string recive = string.Empty; 
        int i = 0;
        modemSerialPort.WriteChars(command.ToCharArray());
        Log.Debug(LogicObject.BrowseName, $"Send command {command}");
        i = 0;
        bool exitWhile = false;
        while (!exitWhile)
        {
            Thread.Sleep(1000);
            recive = modemSerialPort.ReadLine();
            if (!command.Contains("CMGS") && recive.Contains(">")) 
                modemSerialPort.WriteChars(("\x1B").ToCharArray()); // send esc for hangout CMGS
            if (reciveCheckCustom != "")
                exitWhile = recive.Contains(reciveCheckCustom);
            else
                exitWhile = recive.ToUpper().Contains("OK") && !recive.ToUpper().Contains("ERROR");
            i++;
            if (i > 9)
                throw new TimeoutException("Missing reply from the modem");
        }        
        Log.Debug(LogicObject.BrowseName, $"Reply from modem {recive}");
        return recive;
    }

    private void SendMessage()
    {
        while (runningGetModemStatus)
        {
            Thread.Sleep(500);
        }
        runningSendingMessages = true;
        try
        { 
            if (SendSMS(destinationNumber, message))
                Log.Info(LogicObject.BrowseName, "Message sent");
            else
                Log.Error(LogicObject.BrowseName, "Message not sent");
        }
        catch
        {
            Log.Error(LogicObject.BrowseName, "Message not sent");
        }
        runningSendingMessages = false;
    }


    private class memoryAlarm
    {
        public IUANode alarmNode { get; set; }
        public LocalizedText alarmText { get; set; }
        public bool sendON { get; set; }
        public bool sendOFF { get; set; }
        public bool sendACK { get; set; }
        public bool sendHigh { get; set; }
        public bool sendHighHigh { get; set; }
        public bool sendLow { get; set; }
        public bool sendLowLow { get; set; }
        public bool alarmAckState { get; set; }
        public bool alarmConfirmedState { get; set; }
        public bool alarmHighState { get; set; }
        public bool alarmHighHighState { get; set; }
        public bool alarmLowState { get; set; }
        public bool alarmLowLowState { get; set; }

    }
    private class messageQueue
    {
        public LocalizedText alarmText { get; set; }
        public DateTime messageRegistrationDate { get; set; }
        public string alarmStatus { get; set; }
        public int retryCount { get; set; }
    }
    private enum pinStatusEnum
    {
        Unlocked = 0,
        Pin1Required = 1,
        Pin2Required = 3,
        PukRequired = 2,
        Puk2Required = 4,
        Unknown = 255
    }

    private SerialPort modemSerialPort;
    private Group recipent;
    private Folder alarmsFolder;
    private PeriodicTask alarmObserver;
    private PeriodicTask modemStatusCheckTask;
    private PeriodicTask modemQueueWorker;
    private LongRunningTask modemSendMessage;
    private IUANode alarmsNode;
    private List<IUANode> memoryAlarmsNodeList;
    private List<memoryAlarm> memoryAlarmsList;
    private List<messageQueue> messagesToSend;
    private bool runningSendingMessages;
    private bool runningGetModemStatus;
    private bool manualUpdateInfoRequest;
    private bool pinErrorMemory; //to avoid in a single session to lock a SIM
    private string destinationNumber;
    private string message;

    private IUAVariable simPin;
    private IUAVariable pinStatus;
    private IUAVariable registrationStatus;
    private IUAVariable signalStrange;
    private IUAVariable actualCarrier;
    private IUAVariable numberofRetry;
    private IUAVariable initCommand;
    private IUAVariable autoRefreshInfo;
    private IUAVariable autoUnlockPIN;

}


