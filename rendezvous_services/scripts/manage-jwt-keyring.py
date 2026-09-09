#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import secrets
import shutil
import sys
from pathlib import Path
from typing import Dict


class AbortWithoutSave(Exception):
    """Raised when user presses ESC to exit without saving."""


def read_prompt(prompt: str) -> str:
    value = input(prompt)
    if value == "\x1b":
        raise AbortWithoutSave()
    return value


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Manage rendezvous JWT keyring JSON file.")
    parser.add_argument(
        "--file",
        default=str(Path(__file__).resolve().parent.parent / "jwt_keys.json"),
        help="Path to keyring JSON file (default: ../jwt_keys.json).",
    )
    return parser.parse_args()


def load_keyring(path: Path) -> Dict[str, str]:
    if not path.exists():
        return {}

    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as ex:
        raise ValueError(f"Invalid JSON in {path}: {ex}") from ex

    if not isinstance(data, dict):
        raise ValueError(f"Keyring file must contain a JSON object of {{kid: secret}} entries: {path}")

    keyring: Dict[str, str] = {}
    for raw_kid, raw_secret in data.items():
        kid = str(raw_kid).strip()
        secret = str(raw_secret).strip() if raw_secret is not None else ""
        if kid and secret:
            keyring[kid] = secret

    return keyring


def rotate_backups(path: Path) -> None:
    bak1 = path.with_name(path.name + ".bak1")
    bak2 = path.with_name(path.name + ".bak2")
    bak3 = path.with_name(path.name + ".bak3")

    if bak3.exists():
        bak3.unlink()
    if bak2.exists():
        shutil.move(str(bak2), str(bak3))
    if bak1.exists():
        shutil.move(str(bak1), str(bak2))
    if path.exists():
        shutil.move(str(path), str(bak1))


def save_keyring(path: Path, keyring: Dict[str, str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    rotate_backups(path)
    serialized = json.dumps(dict(sorted(keyring.items())), indent=2) + "\n"
    path.write_text(serialized, encoding="utf-8")


def generate_secret() -> str:
    return secrets.token_hex(32)


def print_menu() -> None:
    print("\nJWT Keyring Manager")
    print("1) List entries")
    print("2) Add entry")
    print("3) Rename entry Key ID (keep secret)")
    print("4) Delete entry")
    print("5) Save changes")
    print("6) Quit")


def list_entries(keyring: Dict[str, str]) -> None:
    if not keyring:
        print("No entries found.")
        return

    print(f"{len(keyring)} entr{'y' if len(keyring) == 1 else 'ies'}:")
    for kid in sorted(keyring):
        print(f"- {kid}: {keyring[kid]}")


def prompt_kid(action: str) -> str:
    kid = read_prompt(f"Enter Key ID to {action}: ").strip()
    if not kid:
        print("Key ID cannot be empty.")
        return ""
    return kid


def add_entry(keyring: Dict[str, str]) -> Dict[str, str]:
    kid = prompt_kid("add")
    if not kid:
        return keyring

    if kid in keyring:
        print(f"Entry '{kid}' already exists. Use change instead.")
        return keyring

    secret = generate_secret()
    updated = dict(keyring)
    updated[kid] = secret

    print(f"Added entry: {kid}")
    print(f"Secret: {secret}")
    return updated


def rename_entry_kid(keyring: Dict[str, str]) -> Dict[str, str]:
    old_kid = prompt_kid("rename")
    if not old_kid:
        return keyring

    if old_kid not in keyring:
        print(f"Entry '{old_kid}' was not found.")
        return keyring

    new_kid = read_prompt("Enter new Key ID: ").strip()
    if not new_kid:
        print("New Key ID cannot be empty.")
        return keyring

    if new_kid == old_kid:
        print("New Key ID is the same as current Key ID. No changes made.")
        return keyring

    if new_kid in keyring:
        print(f"Entry '{new_kid}' already exists.")
        return keyring

    updated = dict(keyring)
    secret = updated.pop(old_kid)
    updated[new_kid] = secret

    print(f"Renamed entry: {old_kid} -> {new_kid}")
    return updated


def delete_entry(keyring: Dict[str, str]) -> Dict[str, str]:
    kid = prompt_kid("delete")
    if not kid:
        return keyring

    if kid not in keyring:
        print(f"Entry '{kid}' was not found.")
        return keyring

    confirm = read_prompt(f"Delete '{kid}'? (y/N): ").strip().lower()
    if confirm not in {"y", "yes"}:
        print("Delete cancelled.")
        return keyring

    updated = dict(keyring)
    del updated[kid]
    print(f"Deleted entry: {kid}")
    return updated


def prompt_save_changes(keyring_path: Path, original_keyring: Dict[str, str], working_keyring: Dict[str, str]) -> bool:
    if working_keyring == original_keyring:
        print("No changes to save.")
        return True

    while True:
        response = read_prompt("You have unsaved changes. Save now? (Y/n/c): ").strip().lower()
        if response in {"", "y", "yes"}:
            save_keyring(keyring_path, working_keyring)
            print(f"Saved {len(working_keyring)} entr{'y' if len(working_keyring) == 1 else 'ies'} to {keyring_path}")
            return True
        if response in {"n", "no"}:
            print("Discarded unsaved changes.")
            return True
        if response in {"c", "cancel"}:
            return False

        print("Invalid response. Enter Y, n, or c.")


def save_if_modified(keyring_path: Path, original_keyring: Dict[str, str], working_keyring: Dict[str, str]) -> Dict[str, str]:
    if working_keyring == original_keyring:
        print("No changes to save.")
        return original_keyring

    save_keyring(keyring_path, working_keyring)
    print(f"Saved {len(working_keyring)} entr{'y' if len(working_keyring) == 1 else 'ies'} to {keyring_path}")
    return dict(working_keyring)


def main() -> int:
    args = parse_args()
    keyring_path = Path(args.file).resolve()

    try:
        keyring = load_keyring(keyring_path)
    except ValueError as ex:
        print(str(ex))
        return 1

    original_keyring = dict(keyring)

    print(f"Using keyring file: {keyring_path}")
    print("Press ESC then Enter at any prompt to exit immediately without saving.")

    try:
        while True:
            print_menu()
            choice = read_prompt("Select option [1-6]: ").strip()

            if choice == "1":
                list_entries(keyring)
            elif choice == "2":
                keyring = add_entry(keyring)
                list_entries(keyring)
            elif choice == "3":
                keyring = rename_entry_kid(keyring)
                list_entries(keyring)
            elif choice == "4":
                keyring = delete_entry(keyring)
                list_entries(keyring)
            elif choice == "5":
                original_keyring = save_if_modified(keyring_path, original_keyring, keyring)
            elif choice == "6":
                if not prompt_save_changes(keyring_path, original_keyring, keyring):
                    continue
                print("Exiting keyring manager.")
                return 0
            else:
                print("Invalid option. Select 1, 2, 3, 4, 5, or 6.")
    except AbortWithoutSave:
        print("ESC received. Exiting without saving changes.")
        return 0


if __name__ == "__main__":
    sys.exit(main())
