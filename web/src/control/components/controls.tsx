// ============================================================================
// Hand-styled controls (AC3: zero native-looking widgets).
// Toggle, Slider, Select, Segmented, Button — all custom-rendered.
// ============================================================================
import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import './controls.css';

// ------------------------------------------------------------------ Toggle --

export interface ToggleProps {
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
  ariaLabel?: string;
  /** Hero size for the master power switch. */
  large?: boolean;
}

export function Toggle({ checked, onChange, disabled, ariaLabel, large }: ToggleProps) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={ariaLabel}
      disabled={disabled}
      className={`ctl-toggle${checked ? ' on' : ''}${large ? ' large' : ''}`}
      onClick={() => onChange(!checked)}
    >
      <span className="ctl-toggle-knob" />
    </button>
  );
}

// ------------------------------------------------------------------ Slider --

export interface SliderProps {
  label: string;
  value: number;
  min?: number;
  max?: number;
  step?: number;
  onChange: (value: number) => void;
  /** Formats the value readout; defaults to the raw number. */
  format?: (value: number) => string;
  disabled?: boolean;
}

export function Slider({
  label,
  value,
  min = 0,
  max = 1,
  step = 0.01,
  onChange,
  format,
  disabled,
}: SliderProps) {
  const pct = max === min ? 0 : ((value - min) / (max - min)) * 100;
  return (
    <label className={`ctl-slider${disabled ? ' disabled' : ''}`}>
      <span className="ctl-slider-head">
        <span className="ctl-slider-label">{label}</span>
        <span className="ctl-slider-value">{format ? format(value) : String(value)}</span>
      </span>
      <input
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        disabled={disabled}
        style={{ ['--fill' as string]: `${pct}%` }}
        onChange={(e) => onChange(Number(e.target.value))}
      />
    </label>
  );
}

// ------------------------------------------------------------------ Select --

export interface SelectOption {
  value: string;
  label: string;
}

export interface SelectProps {
  value: string;
  options: SelectOption[];
  onChange: (value: string) => void;
  size?: 'sm' | 'md';
  ariaLabel?: string;
}

export function Select({ value, options, onChange, size = 'md', ariaLabel }: SelectProps) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', close);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const current = options.find((o) => o.value === value);

  return (
    <div ref={rootRef} className={`ctl-select ${size}${open ? ' open' : ''}`}>
      <button
        type="button"
        className="ctl-select-button"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-label={ariaLabel}
        onClick={(e) => {
          e.stopPropagation();
          setOpen((v) => !v);
        }}
      >
        <span className="ctl-select-value">{current?.label ?? value}</span>
        <svg className="ctl-select-chevron" width="10" height="10" viewBox="0 0 10 10">
          <path d="M2 3.5 L5 6.5 L8 3.5" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
        </svg>
      </button>
      {open && (
        <div className="ctl-select-pop" role="listbox">
          {options.map((opt) => (
            <button
              key={opt.value}
              type="button"
              role="option"
              aria-selected={opt.value === value}
              className={`ctl-select-opt${opt.value === value ? ' active' : ''}`}
              onClick={(e) => {
                e.stopPropagation();
                onChange(opt.value);
                setOpen(false);
              }}
            >
              {opt.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

// --------------------------------------------------------------- Segmented --

export interface SegmentedOption {
  value: number;
  label: string;
}

export interface SegmentedProps {
  value: number;
  options: SegmentedOption[];
  onChange: (value: number) => void;
  ariaLabel?: string;
}

export function Segmented({ value, options, onChange, ariaLabel }: SegmentedProps) {
  return (
    <div className="ctl-segmented" role="radiogroup" aria-label={ariaLabel}>
      {options.map((opt) => (
        <button
          key={opt.value}
          type="button"
          role="radio"
          aria-checked={opt.value === value}
          className={`ctl-segment${opt.value === value ? ' active' : ''}`}
          onClick={() => onChange(opt.value)}
        >
          {opt.label}
        </button>
      ))}
    </div>
  );
}

// ------------------------------------------------------------------ Button --

export interface ButtonProps {
  children: ReactNode;
  onClick?: () => void;
  variant?: 'accent' | 'ghost' | 'danger';
  size?: 'sm' | 'md' | 'lg';
  disabled?: boolean;
  title?: string;
}

export function Button({
  children,
  onClick,
  variant = 'ghost',
  size = 'md',
  disabled,
  title,
}: ButtonProps) {
  return (
    <button
      type="button"
      className={`ctl-btn ${variant} ${size}`}
      onClick={onClick}
      disabled={disabled}
      title={title}
    >
      {children}
    </button>
  );
}

// -------------------------------------------------------------- TextInput --

export interface TextInputProps {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  onEnter?: () => void;
  ariaLabel?: string;
}

export function TextInput({ value, onChange, placeholder, onEnter, ariaLabel }: TextInputProps) {
  return (
    <input
      type="text"
      className="ctl-input"
      value={value}
      placeholder={placeholder}
      aria-label={ariaLabel}
      onChange={(e) => onChange(e.target.value)}
      onKeyDown={(e) => {
        if (e.key === 'Enter') onEnter?.();
      }}
      spellCheck={false}
    />
  );
}
