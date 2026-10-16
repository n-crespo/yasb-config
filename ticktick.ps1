<#
.SYNOPSIS
    Show your TickTick tasks for a day (or range of days), sorted by start time
    and displayed in local time, colored by priority.

.DESCRIPTION
    Wraps `ticktick task filter --json`. Tasks are sorted and displayed by their
    START time (falling back to the due date when there is no start), since
    TickTick's dueDate is really the END of the task's duration.

    Colors: none = white, low = blue, medium = yellow, high = red.
    Completed tasks (see -IncludeDone) are shown in grey italics.

.EXAMPLE
    .\ticktick.ps1
    Open tasks that start today.

.EXAMPLE
    .\ticktick.ps1 -Days 3 -Priority 3,5
    Medium/high priority tasks over the next three days.

.EXAMPLE
    .\ticktick.ps1 -Upcoming
    The task in progress right now, or the next one to start.

.EXAMPLE
    .\ticktick.ps1 -Date '2026-10-20' -AsObject | Where-Object Title -like '*LEC*'
    Emit structured objects for further pipeline processing.
#>
[CmdletBinding()]
param(
  # First day to show (local time). Defaults to today.
  [datetime]$Date = (Get-Date).Date,

  # Number of consecutive days to show.
  [ValidateRange(1, 365)]
  [int]$Days = 1,

  # Priority filter: 0 (none), 1 (low), 3 (medium), 5 (high).
  [ValidateSet(0, 1, 3, 5)]
  [int[]]$Priority,

  [string[]]$Tag,

  # Project IDs.
  [string[]]$Project,

  # Include completed tasks as well as open ones.
  [switch]$IncludeDone,

  # Show only one task: the one in progress right now, otherwise the next
  # one to start. Timed tasks win over all-day tasks on the same day, but
  # an all-day task beats timed tasks on later days. Looks 7 days ahead
  # unless you pass -Days.
  [switch]$Upcoming,

  # Emit task objects instead of formatted text.
  [switch]$AsObject,

  # Disable ANSI colors/italics. Also disabled when $env:NO_COLOR is set.
  [switch]$NoColor,

  # Name or path of the TickTick CLI executable.
  [string]$CliPath = 'ticktick'
)

$ErrorActionPreference = 'Stop'

# $PSBoundParameters isn't visible inside functions, so capture this here.
$script:DaysSpecified = $PSBoundParameters.ContainsKey('Days')

# How far -Upcoming looks ahead when -Days isn't given.
$script:UpcomingLookaheadDays = 7

# TickTick status codes, per `ticktick task filter -h`.
$script:StatusOpen      = 0
$script:StatusCompleted = 2

$script:PriorityNames = @{ 0 = 'None'; 1 = 'Low'; 3 = 'Medium'; 5 = 'High' }

# ANSI SGR codes. Edit these to restyle the output.
# (30-37 = standard colors, 90-97 = bright variants.)
$script:PriorityColors = @{
  None   = '37'   # white
  Low    = '34'   # blue
  Medium = '33'   # yellow
  High   = '31'   # red
}

# Completed tasks ignore priority color: grey (bright black) + italic.
$script:DoneStyle = @('90', '3')

# Set by Invoke-Main once the CLI executable has been located.
$script:CliExecutable = $null


#region Date handling

function ConvertTo-LocalDateTime
{
  <#
    Normalizes whatever ConvertFrom-Json gave us into a local [datetime].
    PowerShell 7 auto-parses ISO dates; Windows PowerShell 5.1 leaves strings,
    and TickTick's "+0000" offset needs a colon before .NET will parse it.
    #>
  param([AllowNull()] $Value)

  if ($null -eq $Value -or "$Value" -eq '')
  { return $null
  }

  if ($Value -is [datetimeoffset])
  { return $Value.LocalDateTime
  }
  if ($Value -is [datetime])
  { return $Value.ToLocalTime()
  }

  $iso = $Value -replace '([+-]\d{2})(\d{2})$', '$1:$2'
  return [datetimeoffset]::Parse($iso).LocalDateTime
}

function ConvertTo-UtcIsoString
{
  param([Parameter(Mandatory)] [datetime]$LocalTime)

  $LocalTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
}

function Get-DateRange
{
  <# Local-time range [Start, End) covering $Days whole days from $From. #>
  param(
    [Parameter(Mandatory)] [datetime]$From,
    [Parameter(Mandatory)] [int]$Days
  )

  $start = $From.Date
  [pscustomobject]@{
    Start = $start
    End   = $start.AddDays($Days)
  }
}

#endregion


#region TickTick CLI access

function Resolve-CliExecutable
{
  <#
    Finds the real CLI executable. Only *applications* are considered, so this
    script (ticktick.ps1) can never be picked up by mistake if its folder is
    on your PATH, which would cause it to call itself forever.
    #>
  param([Parameter(Mandatory)] [string]$Name)

  $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1

  if (-not $command)
  {
    throw "Could not find the '$Name' executable. Is it installed and on your PATH? (See -CliPath.)"
  }
  $command.Source
}

function Invoke-TickTickCli
{
  <# Runs the CLI and returns its stdout lines; throws on a non-zero exit. #>
  param([Parameter(Mandatory)] [string[]]$Arguments)

  $output = & $script:CliExecutable @Arguments
  if ($LASTEXITCODE -ne 0)
  {
    throw "'ticktick $($Arguments -join ' ')' failed with exit code $LASTEXITCODE."
  }
  $output
}

function Get-FilterArguments
{
  <# Translates our parameters into `ticktick task filter` arguments. #>
  param(
    [Parameter(Mandatory)] [datetime]$QueryStart,
    [Parameter(Mandatory)] [datetime]$QueryEnd,
    [switch]$IncludeDone,
    [int[]]$Priority,
    [string[]]$Tag,
    [string[]]$Project
  )

  $statuses = @($script:StatusOpen)
  if ($IncludeDone)
  { $statuses += $script:StatusCompleted
  }

  $arguments = @(
    'task', 'filter',
    '--start-date', (ConvertTo-UtcIsoString $QueryStart),
    '--end-date',   (ConvertTo-UtcIsoString $QueryEnd),
    "--status=$($statuses -join ',')",
    '--json'
  )

  # Optional filters: only added when provided.
  if ($Priority)
  { $arguments += '--priority', ($Priority -join ',')
  }
  if ($Tag)
  { $arguments += '--tag',      ($Tag      -join ',')
  }
  if ($Project)
  { $arguments += '--projects', ($Project  -join ',')
  }

  $arguments
}

function Get-RawTickTickTask
{
  <# Returns the CLI's JSON output as parsed objects. #>
  param([Parameter(Mandatory)] [string[]]$FilterArguments)

  $json = (Invoke-TickTickCli -Arguments $FilterArguments) -join "`n"
  if ([string]::IsNullOrWhiteSpace($json))
  { return @()
  }

  @($json | ConvertFrom-Json)
}

#endregion


#region Task model

function ConvertTo-TaskModel
{
  <# Maps a raw TickTick JSON task onto the small model the rest of the script uses. #>
  param(
    [Parameter(Mandatory, ValueFromPipeline)] $Raw,

    # Position in the CLI's output, so we can recover its original order after sorting.
    [int]$Order = 0
  )

  process
  {
    $end   = ConvertTo-LocalDateTime $Raw.dueDate
    $start = ConvertTo-LocalDateTime $Raw.startDate
    if (-not $start)
    { $start = $end
    }

    $priorityName = $script:PriorityNames[[int]$Raw.priority]
    if (-not $priorityName)
    { $priorityName = 'None'
    }

    [pscustomobject]@{
      Id       = $Raw.id
      Title    = $Raw.title
      Priority = $priorityName
      IsDone   = ([int]$Raw.status -eq $script:StatusCompleted)
      IsAllDay = [bool]$Raw.isAllDay
      Start    = $start
      End      = $end
      CliOrder = $Order
      # Tasks with no dates at all sort last.
      SortKey  = if ($start)
      { $start
      } else
      { [datetime]::MaxValue
      }
    }
  }
}

function Test-TaskStartsInRange
{
  param(
    [Parameter(Mandatory)] $Task,
    [Parameter(Mandatory)] $Range
  )

  $Task.Start -and $Task.Start -ge $Range.Start -and $Task.Start -lt $Range.End
}

function Get-TickTickTask
{
  <#
    Returns tasks that START within $Range, sorted by start time.

    The CLI can only filter on dueDate, so we query one extra day past the
    range (to catch tasks that start late and end after midnight) and then
    filter on the real start time locally.
    #>
  param(
    [Parameter(Mandatory)] $Range,
    [switch]$IncludeDone,
    [int[]]$Priority,
    [string[]]$Tag,
    [string[]]$Project
  )

  $filterArguments = Get-FilterArguments `
    -QueryStart  $Range.Start `
    -QueryEnd    $Range.End.AddDays(1) `
    -IncludeDone:$IncludeDone `
    -Priority    $Priority `
    -Tag         $Tag `
    -Project     $Project

  $order = 0
  Get-RawTickTickTask -FilterArguments $filterArguments |
    ForEach-Object { ConvertTo-TaskModel -Raw $_ -Order ($order++) } |
    Where-Object { Test-TaskStartsInRange -Task $_ -Range $Range } |
    Sort-Object SortKey
}

function Select-UpcomingTask
{
  <#
    Picks the single most relevant task for "what's next".

    Timed candidates are tasks in progress right now (they have a duration and
    Now falls inside it) or yet to start; the best one is the earliest by start.

    An all-day task only beats that best timed task if it falls on an EARLIER
    DAY. So on any given day, timed tasks win; but a task due all day today
    beats a timed task tomorrow. If several all-day tasks qualify (or there is
    no timed task at all), the first one in the CLI's own order is used.

    Completed tasks are ignored.
    #>
  param(
    [AllowEmptyCollection()] [object[]]$Task = @(),
    [Parameter(Mandatory)] [datetime]$Now
  )

  $open = @($Task | Where-Object { -not $_.IsDone -and $_.Start })

  # In-progress tasks started before any upcoming one, so sorting by start
  # puts "happening now" ahead of "coming up".
  $bestTimed = $open |
    Where-Object {
      -not $_.IsAllDay -and (
        $_.Start -gt $Now -or
        ($_.End -and $_.End -ne $_.Start -and $_.Start -le $Now -and $Now -lt $_.End)
      )
    } |
    Sort-Object SortKey |
    Select-Object -First 1

  $allDay = @($open | Where-Object IsAllDay | Sort-Object CliOrder)

  # An all-day task has to be on an earlier day than the best timed task to beat it.
  if ($bestTimed)
  {
    $allDay = @($allDay | Where-Object { $_.Start.Date -lt $bestTimed.Start.Date })
  }

  if ($allDay.Count -gt 0)
  { return $allDay[0]
  }
  if ($bestTimed)
  { return $bestTimed
  }
}

#endregion


#region Formatting

function Format-ClockTime
{
  <# 2:00 PM -> "2PM", 2:30 PM -> "2:30PM". -OmitMeridiem drops the AM/PM. #>
  param(
    [Parameter(Mandatory)] [datetime]$Time,
    [switch]$OmitMeridiem
  )

  $culture = [cultureinfo]::InvariantCulture
  $text = if ($Time.Minute -eq 0)
  { $Time.ToString('%h', $culture)
  } else
  { $Time.ToString('h:mm', $culture)
  }

  if ($OmitMeridiem)
  { return $text
  }
  $text + $Time.ToString('tt', $culture)
}

function Format-TimeRange
{
  <# "2 - 4PM", "9AM - 12:30PM", "6:30PM", "all day", or "no time". #>
  param([Parameter(Mandatory)] $Task)

  if ($Task.IsAllDay)
  { return 'all day'
  }
  if (-not $Task.Start)
  { return 'no time'
  }

  $hasDuration = $Task.End -and $Task.End -ne $Task.Start
  if (-not $hasDuration)
  { return Format-ClockTime $Task.Start
  }

  # Only show AM/PM once when both ends are on the same day and in the same half of it.
  $sameMeridiem = ($Task.Start.Date -eq $Task.End.Date) -and
  (($Task.Start.Hour -ge 12) -eq ($Task.End.Hour -ge 12))

  '{0} - {1}' -f (Format-ClockTime $Task.Start -OmitMeridiem:$sameMeridiem), (Format-ClockTime $Task.End)
}

function Format-CompactTime
{
  <# 2:00 PM -> "2pm", 2:30 PM -> "2:30pm". -OmitMeridiem drops the am/pm suffix. #>
  param(
    [Parameter(Mandatory)] [datetime]$Time,
    [switch]$OmitMeridiem
  )

  $culture = [cultureinfo]::InvariantCulture
  $text = if ($Time.Minute -eq 0)
  { $Time.ToString('%h', $culture)
  } else
  { $Time.ToString('h\:mm', $culture)
  }

  if (-not $OmitMeridiem)
  { $text += $Time.ToString('tt', $culture).ToLowerInvariant()
  }
  $text
}

function Format-CompactTimeRange
{
  <# "2-4pm", "10am-12pm", "2:30-4pm", or just "6:30pm" for a task with no duration. #>
  param([Parameter(Mandatory)] $Task)

  if ($Task.IsAllDay)
  { return 'all day'
  }
  if (-not $Task.Start)
  { return 'no time'
  }

  $hasDuration = $Task.End -and $Task.End -ne $Task.Start
  if (-not $hasDuration)
  { return Format-CompactTime $Task.Start
  }

  # Only the second time carries am/pm when both are in the same half of the day.
  $sameMeridiem = ($Task.Start.Hour -ge 12) -eq ($Task.End.Hour -ge 12)
  '{0}-{1}' -f (Format-CompactTime $Task.Start -OmitMeridiem:$sameMeridiem), (Format-CompactTime $Task.End)
}

function Get-TaskStyleCode
{
  <# Returns the ANSI SGR codes for a task: grey italic if done, else its priority color. #>
  param([Parameter(Mandatory)] $Task)

  if ($Task.IsDone)
  { return $script:DoneStyle
  }

  $color = $script:PriorityColors[$Task.Priority]
  if (-not $color)
  { $color = $script:PriorityColors['None']
  }
  @($color)
}

function Add-AnsiStyle
{
  param(
    [Parameter(Mandatory)] [string]$Text,
    [Parameter(Mandatory)] [string[]]$Code
  )

  $esc = [char]27
  "$esc[$($Code -join ';')m$Text$esc[0m"
}

function Format-TaskLine
{
  param(
    [Parameter(Mandatory, ValueFromPipeline)] $Task,
    [switch]$IncludeDate,
    [switch]$NoColor,

    # Tight single-line format with no column padding, e.g. "2-4pm: Task name".
    [switch]$Compact
  )

  process
  {
    if ($Compact)
    {
      $line = '{0}: {1}' -f (Format-CompactTimeRange $Task), $Task.Title
      if ($IncludeDate -and $Task.Start)
      { $line = '{0:ddd} {1}' -f $Task.Start, $line
      }
    } else
    {
      $line = '{0,-17} {1}' -f (Format-TimeRange $Task), $Task.Title

      if ($IncludeDate)
      {
        $day = if ($Task.Start)
        { '{0:ddd MMM d}' -f $Task.Start
        } else
        { ''
        }
        $line = '{0,-11} {1}' -f $day, $line
      }
    }

    if ($NoColor)
    { return $line
    }
    Add-AnsiStyle -Text $line -Code (Get-TaskStyleCode $Task)
  }
}

#endregion


#region Main

function Invoke-Main
{
  $script:CliExecutable = Resolve-CliExecutable $CliPath

  $rangeDays = $Days
  if ($Upcoming -and -not $script:DaysSpecified)
  {
    $rangeDays = $script:UpcomingLookaheadDays
  }

  $range = Get-DateRange -From $Date -Days $rangeDays

  $tasks = @(Get-TickTickTask `
      -Range       $range `
      -IncludeDone:$IncludeDone `
      -Priority    $Priority `
      -Tag         $Tag `
      -Project     $Project)

  if ($Upcoming)
  {
    $tasks = @(Select-UpcomingTask -Task $tasks -Now (Get-Date))
  }

  if ($AsObject)
  { return $tasks
  }

  if ($tasks.Count -eq 0)
  {
    Write-Host $(if ($Upcoming)
      { 'No upcoming tasks.'
      } else
      { 'No tasks.'
      })
    return
  }

  # Show the date for multi-day lists, or for an upcoming task that isn't today.
  $showDate = if ($Upcoming)
  { $tasks[0].Start.Date -ne (Get-Date).Date
  } else
  { $rangeDays -gt 1
  }

  $plain = $NoColor -or $env:NO_COLOR
  $tasks | Format-TaskLine -IncludeDate:$showDate -NoColor:$plain -Compact:$Upcoming
}

Invoke-Main

#endregion



