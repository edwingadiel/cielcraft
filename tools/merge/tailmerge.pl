use strict; use warnings;
# Usage: perl tailmerge.pl <file> <branch> "<banner line text>"
# Rebuilds <file> as HEAD's version plus the branch's block that starts at the
# banner and runs to the end of the outer class (the last "}" of the file).
my ($file, $branch, $banner) = @ARGV;
local $/;
my $head = `git show HEAD:$file`; die "head" unless length $head;
my $theirs = `git show $branch:$file`; die "theirs" unless length $theirs;
$head =~ s/\r\n/\n/g; $theirs =~ s/\r\n/\n/g;
my $q = quotemeta($banner);
my ($block) = $theirs =~ /(^[ \t]*$q\n.*)\n\}\n\z/ms or die "banner block not found";
$head =~ s/\n\}\n\z/\n\n$block\n}\n/ or die "head tail";
$head =~ s/\n/\r\n/g;
open my $o, '>:raw', $file or die; print $o $head; close $o;
print "$file: appended block from $branch\n";
