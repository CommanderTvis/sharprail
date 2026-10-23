# SharpRail shell integration — written for SharpRail.
# Part of SharpRail and distributed under SharpRail's license.
#
# The terminal host starts zsh with ZDOTDIR pointing here, keeping the user's own ZDOTDIR (if any) in
# SHARPRAIL_SHELL_ZDOTDIR. This file puts the user's ZDOTDIR back, runs their .zshenv, and in interactive shells
# installs hooks that mark the prompt (OSC 133 A/B), command start (C) and end with its status (D), and report the
# working directory (OSC 7). zsh then reads .zprofile, .zshrc and .zlogin from the restored ZDOTDIR as usual.

if [[ -n ${SHARPRAIL_SHELL_ZDOTDIR+set} ]]; then
  ZDOTDIR=$SHARPRAIL_SHELL_ZDOTDIR
  unset SHARPRAIL_SHELL_ZDOTDIR
else
  unset ZDOTDIR
fi

# The user's .zshenv, from where zsh itself would have read it.
if [[ -f ${ZDOTDIR:-$HOME}/.zshenv && -r ${ZDOTDIR:-$HOME}/.zshenv ]]; then
  builtin source -- "${ZDOTDIR:-$HOME}/.zshenv"
fi

if [[ -o interactive && -z ${_sharprail_integrated-} ]]; then
  typeset -g _sharprail_integrated=1
  # Set by preexec, cleared once the finished command's status is reported: an empty Enter reports nothing.
  typeset -g _sharprail_running=
  typeset -g _sharprail_mark_a=$'%{\e]133;A\a%}' _sharprail_mark_b=$'%{\e]133;B\a%}'

  _sharprail_report_cwd() {
    emulate -L zsh
    local LC_ALL=C dir=$PWD out= c i
    for (( i = 1; i <= ${#dir}; i++ )); do
      c=${dir[i]}
      case $c in
        [-/._~A-Za-z0-9]) out+=$c ;;
        *) printf -v c '%%%02X' "'$c"; out+=$c ;;
      esac
    done
    printf '\e]7;file://%s%s\a' "${HOST%%.*}" "$out"
  }

  # Runs first among the precmd hooks, so $? is still the command's status.
  _sharprail_precmd_first() {
    local ret=$?
    if [[ -n $_sharprail_running ]]; then
      _sharprail_running=
      printf '\e]133;D;%s\a' "$ret"
    fi
    return $ret
  }

  # Runs last, after any prompt theme has rebuilt PS1, and marks it again when the marks are gone.
  _sharprail_precmd_last() {
    local ret=$?
    if [[ $PS1 != *$'\e]133;A\a'* ]]; then
      PS1=$_sharprail_mark_a$PS1$_sharprail_mark_b
    fi
    # Keep this hook last and the status hook first even when others were added since.
    if [[ ${precmd_functions[-1]} != _sharprail_precmd_last || ${precmd_functions[1]} != _sharprail_precmd_first ]]; then
      precmd_functions=(_sharprail_precmd_first ${precmd_functions:#_sharprail_precmd_*} _sharprail_precmd_last)
    fi
    return $ret
  }

  _sharprail_preexec() {
    _sharprail_running=1
    printf '\e]133;C\a'
  }

  typeset -ga precmd_functions preexec_functions chpwd_functions
  precmd_functions=(_sharprail_precmd_first ${precmd_functions:#_sharprail_precmd_*} _sharprail_precmd_last)
  preexec_functions+=(_sharprail_preexec)
  chpwd_functions+=(_sharprail_report_cwd)
  _sharprail_report_cwd
fi
