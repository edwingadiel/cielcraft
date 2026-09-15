use strict; use warnings;
# Keep both sides of every conflict hunk (ours first) in the given files.
for my $path (@ARGV) {
    open my $fh, '<:raw', $path or die "$path: $!"; local $/; my $s = <$fh>; close $fh;
    my $n = 0;
    $s =~ s{<<<<<<< HEAD\r?\n(.*?)=======\r?\n(.*?)>>>>>>> [^\r\n]*\r?\n}{ $n++; $1 . $2 }gse;
    open $fh, '>:raw', $path or die; print $fh $s; close $fh;
    print "$path: $n hunks kept both\n";
}
