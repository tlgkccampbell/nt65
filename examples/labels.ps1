# Reads the label files the examples' tests find addresses in. Each test dot-sources this script.
# A label file is the one `nt65 remap-dbg --labels` writes, which names each address by its path
# in the source, such as `wave::shown`, whether or not the module exports it. ld65's own label
# file names a name that is not exported without its module, so two modules' names can meet in it.

# Returns the labels in a label file, each name with its address, from lines such as
# `al 7E2388 .wave::scrolls`. A name the file gives two addresses is kept, so that looking it up
# can say so.
function Read-Labels([string]$file) {
    if (-not (Test-Path $file)) { throw "there is no label file at ${file}: build first" }
    $labels = [Collections.Generic.Dictionary[string, Collections.Generic.List[int]]]::new([StringComparer]::Ordinal)
    foreach ($line in Get-Content $file) {
        $_, $address, $name = $line -split ' '
        $name = $name.TrimStart('.')
        if (-not $labels.ContainsKey($name)) { $labels[$name] = [Collections.Generic.List[int]]::new() }
        $value = [Convert]::ToInt32($address, 16)
        if ($value -notin $labels[$name]) { $labels[$name].Add($value) }
    }
    return , $labels
}

# Returns the address of a name, such as `wave::shown`, from labels Read-Labels returned. Stops
# the test if no label has the name, or if more than one address does.
function Address($labels, [string]$name) {
    if (-not $labels.ContainsKey($name)) { throw "no label is named $name" }
    $addresses = $labels[$name]
    if ($addresses.Count -gt 1) {
        throw "$name names more than one address: $(($addresses | ForEach-Object { '${0:X}' -f $_ }) -join ', ')"
    }
    return $addresses[0]
}
