// versioninfo writes the Windows version resource that Build.ps1 links into tailnet-helper.exe (a .syso beside its source).
// Windows shows its description where a person sees the helper: Windows Firewall's prompt when the node first opens its
// port, Task Manager and the file's Details. Without it, they show the bare file name.
//
//	go run . -out <file.syso> -version <a.b.c[.d]>
package main

import (
	"flag"
	"fmt"
	"os"

	"github.com/tc-hib/winres"
	"github.com/tc-hib/winres/version"
)

const description = "Message Link (Tailscale)"

func main() {
	out := flag.String("out", "", "the .syso to write")
	ver := flag.String("version", "", "the app's version")
	flag.Parse()
	if *out == "" || *ver == "" {
		fmt.Fprintln(os.Stderr, "usage: versioninfo -out <file.syso> -version <a.b.c>")
		os.Exit(2)
	}
	var vi version.Info
	vi.SetFileVersion(*ver)
	vi.SetProductVersion(*ver)
	for key, value := range map[string]string{
		version.FileDescription:  description,
		version.ProductName:      "Message Link",
		version.CompanyName:      "wrenchware",
		version.OriginalFilename: "tailnet-helper.exe",
		version.InternalName:     "tailnet-helper",
	} {
		if err := vi.Set(0x0409, key, value); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
	}
	var rs winres.ResourceSet
	rs.SetVersionInfo(vi)
	f, err := os.Create(*out)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	if err := rs.WriteObject(f, winres.ArchAMD64); err != nil {
		f.Close()
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	if err := f.Close(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
