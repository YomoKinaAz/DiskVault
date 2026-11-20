# UI Automation で DiskVault の画面を操作する小道具（GUI テスト用）
param([string]$Action, [string]$Name, [string]$Window = "DiskVault")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$p = Get-Process DiskVault | Select-Object -First 1
$cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
function Find($n) {
    foreach ($w in $wins) {
        $c = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $n)
        $e = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
        if ($e) { return $e }
    }
    throw "見つかりません: $n"
}
switch ($Action) {
    "click" { (Find $Name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    "select" { (Find $Name).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    "combo" {
        $cb = $null
        foreach ($w in $wins) { $cb = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Window)); if ($cb) { break } }
        $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 300
        $items = $cb.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
        $it = $items | Where-Object { $_.Current.Name -like "*$Name*" } | Select-Object -First 1
        $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    }
    "windows" { $wins | ForEach-Object { $_.Current.Name } }
}

