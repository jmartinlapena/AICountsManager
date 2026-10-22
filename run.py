import subprocess
import sys
from pathlib import Path


def main() -> int:
    project = Path(__file__).resolve().parent / "src" / "AicountsManager.App" / "AicountsManager.App.csproj"
    command = ["dotnet", "run", "--project", str(project)]
    return subprocess.call(command)


if __name__ == "__main__":
    raise SystemExit(main())
