# Installs or removes the "Every Printer" printer. Needs administrator rights.
#   printer-setup.ps1 -Action Install -Inbox <folder> -Exe <EveryPrinter.exe> -User <DOMAIN\name>
#   printer-setup.ps1 -Action Remove  -Inbox <folder>
# The printer uses Windows' own "Microsoft Print To PDF" driver on a file port inside the inbox folder.
# A scheduled task starts Every Printer whenever a job is printed to it, so printing works even when the app is closed.
param(
    [Parameter(Mandatory)][ValidateSet('Install', 'Remove')][string]$Action,
    [Parameter(Mandatory)][string]$Inbox,
    [string]$Exe,
    [string]$User
)
$ErrorActionPreference = 'Stop'
$printerName = 'Every Printer'
$taskName = 'Every Printer - print job'
$port = Join-Path $Inbox 'incoming.pdf'

if ($Action -eq 'Install') {
    if (-not (Test-Path $Exe)) { throw "Cannot find $Exe" }
    if (-not (Get-PrinterDriver -Name 'Microsoft Print To PDF' -ErrorAction SilentlyContinue)) {
        throw 'The Microsoft Print to PDF driver is not installed. Turn it on in Windows Features.'
    }
    New-Item -ItemType Directory -Force -Path $Inbox | Out-Null
    # The inbox is written by the print spooler (SYSTEM) and read and moved by the user.
    if ($User) { & icacls.exe $Inbox /grant "${User}:(OI)(CI)M" | Out-Null }

    if (-not (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue)) { Add-PrinterPort -Name $port }
    if (-not (Get-Printer -Name $printerName -ErrorAction SilentlyContinue)) {
        Add-Printer -Name $printerName -DriverName 'Microsoft Print To PDF' -PortName $port
    }

    # Event 307 ("document printed") is only logged when the operational log is on.
    & wevtutil.exe sl 'Microsoft-Windows-PrintService/Operational' /e:true
    $query = "<QueryList><Query Id=`"0`" Path=`"Microsoft-Windows-PrintService/Operational`">" +
        "<Select Path=`"Microsoft-Windows-PrintService/Operational`">*[System[EventID=307]] and *[UserData/DocumentPrinted[Param5='$printerName']]</Select>" +
        "</Query></QueryList>"
    $query = [Security.SecurityElement]::Escape($query)
    $exeXml = [Security.SecurityElement]::Escape($Exe)
    $userXml = if ($User) { "<UserId>$([Security.SecurityElement]::Escape($User))</UserId><LogonType>InteractiveToken</LogonType>" } else { '<GroupId>S-1-5-32-545</GroupId>' }
    $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers><EventTrigger><Enabled>true</Enabled><Subscription>$query</Subscription></EventTrigger></Triggers>
  <Principals><Principal id="Author">$userXml<RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <StartWhenAvailable>true</StartWhenAvailable>
  </Settings>
  <Actions Context="Author"><Exec><Command>$exeXml</Command><Arguments>--inbox</Arguments></Exec></Actions>
</Task>
"@
    Register-ScheduledTask -TaskName $taskName -Xml $xml -Force | Out-Null
}
else {
    $ErrorActionPreference = 'SilentlyContinue'
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Remove-Printer -Name $printerName
    Remove-PrinterPort -Name $port
}
