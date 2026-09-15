use strict; use warnings;
# Usage: perl addstubs.pl build-errors.txt
# For every "'X.FakeBridge' does not implement interface member 'IGameBridge.M'" error,
# appends a throwing stub (copied from the interface declaration) to that test file's
# FakeBridge, right after its "Unexpected()" helper line.
my ($errfile) = @ARGV;
open my $e, '<', $errfile or die; my @errors = <$e>; close $e;
open my $i, '<:raw', 'CielCraft/Game/IGameBridge.cs' or die; local $/; my $iface = <$i>; close $i; $/ = "\n";
$iface =~ s/\r\n/\n/g;

my %todo; # file => [member names]
for (@errors) {
    next unless /^(.*?)\(\d+,\d+\): error CS0535: '.*?FakeBridge' does not implement interface member 'IGameBridge\.([A-Za-z0-9_]+)/;
    my ($file, $member) = ($1, $2);
    $file =~ s/\\/\//g;
    push @{ $todo{$file} }, $member unless grep { $_ eq $member } @{ $todo{$file} || [] };
}

for my $file (keys %todo) {
    open my $fh, '<:raw', $file or die "$file: $!"; local $/; my $s = <$fh>; close $fh; $/ = "\n";
    my $crlf = $s =~ /\r\n/; $s =~ s/\r\n/\n/g;
    my @stubs;
    for my $m (@{ $todo{$file} }) {
        # property: "    <type> Name { get; }"   method: "    <ret> Name(<params>);"
        if ($iface =~ /^\s+([^\n;{]*?\b\Q$m\E)\s*\{\s*get;\s*\}/m) {
            push @stubs, "        public $1 => throw Unexpected();";
        } elsif ($iface =~ /^\s+([^\n;{]*?\b\Q$m\E\s*\([^;]*?\))\s*;/m) {
            (my $decl = $1) =~ s/\s+/ /g;
            push @stubs, "        public $decl => throw Unexpected();";
        } else {
            die "no declaration found for $m";
        }
    }
    my $block = "\n        // ---- stubs for members other packages added (merge) ----\n" . join("\n", @stubs) . "\n";
    $s =~ s/(        private static NotImplementedException Unexpected\(\) => new\([^\n]*\);\n)/$1$block/ or die "no Unexpected() helper in $file";
    $s =~ s/\n/\r\n/g if $crlf;
    open $fh, '>:raw', $file or die; print $fh $s; close $fh;
    print "$file: +" . scalar(@stubs) . " stubs\n";
}
