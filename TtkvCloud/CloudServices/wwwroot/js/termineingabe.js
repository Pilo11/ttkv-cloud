// Termineingabe: type or pick the Termin of a Begegnung; every change is saved right away
// (POST to the page's Termin handler, JSON answer). The server checks everything again.
(() => {
    "use strict";

    const root = document.querySelector("[data-eingabe]");
    if (!root) {
        return;
    }

    // ---- Input format: mirrors Services/Appointment/TermineingabeInput.cs (same rules and messages) ----

    const WEEKDAYS = ["Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag", "Sonntag"];
    const SHORT = ["Mo", "Di", "Mi", "Do", "Fr", "Sa", "So"];
    const FORMAT_ERROR = "Bitte Datum und Uhrzeit im Format TT.MM.JJJJ HH:MM eingeben, z. B. 14.01.2027 19:30.";
    const MISSING_TIME_ERROR = "Bitte auch die Uhrzeit angeben, z. B. 14.01.2027 19:30.";
    // 1 weekday, 2-4 d.m.y, 5-7 y-m-d, 8-9 time.
    const PATTERN = /^(?:([a-zäöüß]+)\.?\s*,?\s*)?(?:([0-9]{1,2})\.([0-9]{1,2})\.([0-9]{4}|[0-9]{2})|([0-9]{4})-([0-9]{1,2})-([0-9]{1,2}))(?:(?:\s*,\s*|\s+|t)(?:um\s+)?([0-9]{1,2})[:.]([0-9]{2})(?:\s*uhr)?)?$/i;

    const pad = (value, length = 2) => String(value).padStart(length, "0");

    const weekdayIndexOf = (typed) => typed.length < 2
        ? -1
        : WEEKDAYS.findIndex((name) => name.toLowerCase().startsWith(typed.toLowerCase()));

    /** {stamp, display, weekday} ("" for an empty text) or {error}. */
    const parseTermin = (raw) => {
        const text = (raw || "").replace(/\s+/g, " ").trim();
        if (text === "") {
            return { stamp: "", display: "", weekday: "" };
        }

        const m = PATTERN.exec(text);
        if (!m) {
            return { error: FORMAT_ERROR };
        }

        const day = Number(m[2] ?? m[7]);
        const month = Number(m[3] ?? m[6]);
        const yearText = m[4] ?? m[5];
        const year = Number(yearText) + (yearText.length === 2 ? 2000 : 0);
        const dateText = `${pad(day)}.${pad(month)}.${pad(year, 4)}`;
        const date = new Date(0);
        date.setUTCFullYear(year, month - 1, day);
        if (year < 1 || month < 1 || month > 12 || day < 1
            || date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) {
            return { error: `Den ${dateText} gibt es nicht.` };
        }

        const weekday = (date.getUTCDay() + 6) % 7;
        if (m[1]) {
            const typed = weekdayIndexOf(m[1]);
            if (typed < 0) {
                return { error: FORMAT_ERROR };
            }
            if (typed !== weekday) {
                return { error: `Der ${dateText} ist ein ${WEEKDAYS[weekday]}, kein ${WEEKDAYS[typed]}.` };
            }
        }

        if (m[8] === undefined) {
            return { error: MISSING_TIME_ERROR };
        }

        const hour = Number(m[8]);
        const minute = Number(m[9]);
        if (hour > 23 || minute > 59) {
            return { error: `Die Uhrzeit ${pad(hour)}:${m[9]} gibt es nicht.` };
        }

        const time = `${pad(hour)}:${pad(minute)}`;
        return {
            stamp: `${pad(year, 4)}-${pad(month)}-${pad(day)}T${time}`,
            display: `${dateText} ${time}`,
            weekday: SHORT[weekday]
        };
    };

    // ---- Typing aid: digits only is enough, the separators come by themselves ----
    // "140120271930" becomes "14.01.2027 19:30", "5327 905" becomes "5.3.27 9:05". A typed separator (".", ",", ":" or space) ends the
    // current field early and becomes the right one ("1.1.27 19.30" -> "1.1.27 19:30"); a second one is
    // dropped. Text with letters or dashes (weekday, "Uhr", ISO date) stays as typed.

    const DAY = /^\d{1,2}$/;
    const MONTH = /^\d{1,2}\.\d{1,2}$/;
    const YEAR = /^\d{1,2}\.\d{1,2}\.\d{1,4}$/;
    const HOUR = /^\d{1,2}\.\d{1,2}\.\d{1,4} \d{1,2}$/;
    const COMPLETE = /^\d{1,2}\.\d{1,2}\.\d{1,4} \d{1,2}:\d{2}$/;
    // A field is full after two digits (year: four), or after one when no second can follow
    // (day 4-9, month 2-9, hour 3-9): "5" -> "5.", "905" -> "9:05".
    const FIELD_FULL = /^(?:\d{2}|[4-9]|\d{1,2}\.(?:\d{2}|[2-9])|\d{1,2}\.\d{1,2}\.\d{4}|\d{1,2}\.\d{1,2}\.\d{1,4} (?:\d{2}|[3-9]))$/;

    /** The separator that ends the field being typed, or null after the minutes. */
    const nextSeparator = (text) =>
        DAY.test(text) || MONTH.test(text) ? "." : YEAR.test(text) ? " " : HOUR.test(text) ? ":" : null;

    const formatTyped = (raw) => {
        let text = "";
        for (const ch of raw) {
            if (ch >= "0" && ch <= "9") {
                if (COMPLETE.test(text)) {
                    continue;
                }
                text += (FIELD_FULL.test(text) ? nextSeparator(text) : "") + ch;
            } else if (text !== "" && !/[.: ]$/.test(text)) {
                text += nextSeparator(text) ?? "";
            }
        }
        return FIELD_FULL.test(text) && /[0-9]$/.test(raw) ? text + nextSeparator(text) : text;
    };

    /** Only while typing or pasting at the end, so editing in the middle keeps the caret where it is. */
    const autoSeparate = (input, event) => {
        const value = input.value;
        if (event.isComposing
            || (event.inputType && !event.inputType.startsWith("insert"))
            || input.selectionStart !== value.length
            || input.selectionEnd !== value.length
            || !/^[0-9.,: ]*$/.test(value)) {
            return;
        }

        const formatted = formatTyped(value);
        if (formatted !== value) {
            input.value = formatted;
            input.setSelectionRange(formatted.length, formatted.length);
        }
    };

    // ---- Saving ----

    const AUTOSAVE_DELAY = 1000;
    const DEFAULT_TIME = "20:00";
    const saveUrl = root.dataset.saveUrl;
    const token = root.dataset.token;
    const state = root.querySelector("[data-save-state]");
    const rows = [];
    let savedOnce = false;

    /** Where the native picker opens for a Begegnung without Termin: today (inside the Zeitraum) at 20:00. */
    const defaultPickerValue = (picker) => {
        const now = new Date();
        let value = `${pad(now.getFullYear(), 4)}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}T${DEFAULT_TIME}`;
        if (picker.min && value < picker.min) {
            value = picker.min.slice(0, 10) + "T" + DEFAULT_TIME;
        } else if (picker.max && value > picker.max) {
            value = picker.max.slice(0, 10) + "T" + DEFAULT_TIME;
        }
        return value;
    };

    const refreshSummary = () => {
        const busy = rows.filter((row) => row.busy).length;
        const invalid = rows.filter((row) => row.invalid).length;
        if (busy > 0) {
            state.dataset.kind = "saving";
            state.textContent = "Speichert …";
        } else if (invalid > 0) {
            state.dataset.kind = "error";
            state.textContent = invalid === 1 ? "1 Termin nicht gespeichert" : `${invalid} Termine nicht gespeichert`;
        } else if (savedOnce) {
            state.dataset.kind = "ok";
            state.textContent = "Alle Änderungen gespeichert";
        } else {
            delete state.dataset.kind;
            state.textContent = "";
        }

        const all = root.querySelectorAll("tbody tr");
        const done = root.querySelectorAll('tbody tr[data-done="true"]').length;
        const homeDone = root.querySelectorAll('tbody tr[data-home="true"][data-done="true"]').length;
        document.querySelectorAll("[data-count-done]").forEach((el) => { el.textContent = done; });
        document.querySelectorAll("[data-count-home-done]").forEach((el) => { el.textContent = homeDone; });
        document.querySelectorAll("[data-count-bar]").forEach((el) => {
            el.style.width = (all.length === 0 ? 0 : Math.round(100 * done / all.length)) + "%";
        });
    };

    const setupRow = (cell) => {
        const tr = cell.closest("tr");
        const text = cell.querySelector("[data-text]");
        const picker = cell.querySelector("[data-picker]");
        const pickButton = cell.querySelector("[data-pick]");
        const weekday = cell.querySelector("[data-wd]");
        const msg = tr.querySelector("[data-msg]");
        const row = {
            saved: cell.dataset.stamp || "",
            savedDisplay: text.value,
            savedWeekday: weekday.textContent,
            busy: false,
            again: false,
            invalid: false,
            dirty: false,
            timer: 0,
            msgTimer: 0
        };
        rows.push(row);

        const setMessage = (kind, message) => {
            clearTimeout(row.msgTimer);
            if (kind) {
                msg.dataset.kind = kind;
            } else {
                delete msg.dataset.kind;
            }
            msg.textContent = message || "";
        };

        const showError = (message) => {
            row.invalid = true;
            text.setAttribute("aria-invalid", "true");
            setMessage("error", message);
        };

        const clearError = () => {
            row.invalid = false;
            text.removeAttribute("aria-invalid");
            if (msg.dataset.kind === "error") {
                setMessage(null, "");
            }
        };

        const showSaved = (stamp, display, weekdayText) => {
            row.saved = stamp;
            row.savedDisplay = display;
            row.savedWeekday = weekdayText;
            picker.value = stamp || defaultPickerValue(picker);
            if (stamp) {
                tr.dataset.done = "true";
            } else {
                delete tr.dataset.done;
            }
        };

        const save = async () => {
            if (row.busy) {
                row.again = true;
                return;
            }

            const parsed = parseTermin(text.value);
            if (parsed.error) {
                return;
            }

            if (parsed.stamp === row.saved) {
                row.dirty = false;
                refreshSummary();
                return;
            }

            row.busy = true;
            setMessage("saving", "Speichert …");
            refreshSummary();

            let result;
            try {
                const response = await fetch(saveUrl, {
                    method: "POST",
                    credentials: "same-origin",
                    headers: {
                        "Content-Type": "application/x-www-form-urlencoded;charset=UTF-8",
                        "RequestVerificationToken": token
                    },
                    body: new URLSearchParams({ matchId: cell.dataset.match, value: text.value.trim(), expected: row.saved })
                });
                result = response.ok
                    ? await response.json()
                    : { error: "Nicht gespeichert (Fehler " + response.status + "). Bitte die Seite neu laden." };
            } catch {
                result = { error: "Nicht gespeichert – keine Verbindung. Bitte erneut versuchen." };
            }

            row.busy = false;
            if (result.ok || result.conflict) {
                savedOnce = true;
                showSaved(result.stamp, result.value, result.weekday);
            }

            if (result.ok) {
                const now = parseTermin(text.value);
                row.dirty = !!now.error || now.stamp !== row.saved;
                clearError();
                if (!row.dirty) {
                    setMessage("ok", result.stamp ? "Gespeichert" : "Termin entfernt");
                    row.msgTimer = setTimeout(() => setMessage(null, ""), 2500);
                    if (document.activeElement !== text) {
                        text.value = result.value;
                        weekday.textContent = result.weekday;
                    }
                }
            } else if (result.conflict) {
                row.dirty = false;
                row.again = false;
                text.value = result.value;
                weekday.textContent = result.weekday;
                text.removeAttribute("aria-invalid");
                row.invalid = false;
                setMessage("warn", result.error);
            } else {
                row.dirty = true;
                showError(result.error || "Nicht gespeichert.");
            }

            refreshSummary();
            if (row.again) {
                row.again = false;
                save();
            }
        };

        // Blur, Enter or a picked value: check now, show what is wrong, save what is valid.
        const commit = () => {
            clearTimeout(row.timer);
            const parsed = parseTermin(text.value);
            if (parsed.error) {
                showError(parsed.error);
                refreshSummary();
                return;
            }

            clearError();
            text.value = parsed.display;
            weekday.textContent = parsed.weekday;
            save();
        };

        text.addEventListener("input", (event) => {
            autoSeparate(text, event);
            row.dirty = true;
            clearError();
            clearTimeout(row.timer);
            // Save on a pause while typing, but only a complete Termin; errors wait for blur/Enter.
            row.timer = setTimeout(() => {
                const parsed = parseTermin(text.value);
                if (!parsed.error) {
                    weekday.textContent = parsed.weekday;
                    save();
                }
            }, AUTOSAVE_DELAY);
            refreshSummary();
        });

        text.addEventListener("change", commit);

        text.addEventListener("keydown", (event) => {
            if (event.key === "Enter") {
                event.preventDefault();
                commit();
            } else if (event.key === "Escape" && row.dirty && !row.busy) {
                clearTimeout(row.timer);
                text.value = row.savedDisplay;
                weekday.textContent = row.savedWeekday;
                row.dirty = false;
                clearError();
                refreshSummary();
            }
        });

        // The native date/time picker: the datetime-local lies invisibly over the calendar button,
        // so a click or tap opens it directly; the button itself serves the keyboard.
        const openPicker = () => {
            try {
                picker.showPicker();
            } catch {
                picker.focus();
            }
        };
        pickButton.addEventListener("click", openPicker);
        picker.addEventListener("click", openPicker);
        picker.addEventListener("change", () => {
            const parsed = parseTermin(picker.value);
            if (parsed.error) {
                return;
            }

            text.value = parsed.display;
            weekday.textContent = parsed.weekday;
            row.dirty = true;
            commit();
        });

        if (!picker.value) {
            picker.value = defaultPickerValue(picker);
        }
    };

    root.querySelectorAll("[data-termin]").forEach(setupRow);

    const onlyMine = root.querySelector("[data-only-mine]");
    onlyMine?.addEventListener("change", () => {
        if (onlyMine.checked) {
            root.dataset.onlyMine = "true";
        } else {
            delete root.dataset.onlyMine;
        }
    });

    window.addEventListener("beforeunload", (event) => {
        if (rows.some((row) => row.busy || row.dirty)) {
            event.preventDefault();
            event.returnValue = "";
        }
    });
})();
