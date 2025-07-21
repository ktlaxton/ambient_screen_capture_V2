# Data Models

**ApplicationSettings**
Purpose: To store all user-configurable settings, allowing them to be saved to a file and loaded at startup.

Key Attributes: IsEnabled (bool), SelectedEffectId (string), AudioSensitivity (float), SourceMonitorId (string), TargetMonitorIds (List

**EffectStyle**
Purpose: To represent a single visual effect that a user can choose.

Key Attributes: Id (string), Name (string), Description (string).

**DisplayMonitor**
Purpose: To store information about a single physical display connected to the user's computer.

Key Attributes: Id (string), Name (string), IsPrimary (bool).
