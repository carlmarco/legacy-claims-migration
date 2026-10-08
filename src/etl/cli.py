import argparse


def main() -> None:
    parser = argparse.ArgumentParser(prog="etl")
    parser.add_subparsers(dest="command", required=True)
    parser.parse_args()


if __name__ == "__main__":
    main()
