﻿import Vue from 'vue';
import { Component, Prop } from 'vue-property-decorator';
import { FormState } from '../../vue-form';

interface RegisterViewModel {
    email: string,
    password: string,
    confirmPassword: string,
    returnUrl: string,
    redirect: boolean,
    errors: Object
}

@Component
export default class RegisterComponent extends Vue {
    formstate: FormState = {};

    @Prop()
    query: any

    model: RegisterViewModel = {
        email: '',
        password: '',
        confirmPassword: '',
        returnUrl: this.query ? this.query.returnUrl || '' : '',
        redirect: false,
        errors: {}
    };

    fieldClassName(field) {
        if (!field) {
            return '';
        }
        if ((field.$touched || field.$submitted) && field.$valid) {
            return 'text-success';
        }
        if ((field.$touched || field.$submitted) && field.$invalid) {
            return 'text-danger';
        }
    }

    modelErrorValidator(value) {
        return !this.getModelError('*');
    }
    emailModelErrorValidator(value) {
        return !this.getModelError('Email');
    }
    passwordModelErrorValidator(value) {
        return !this.getModelError('Password');
    }
    getModelError(prop: string) {
        return this.model.errors[prop];
    }

    passwordMatch(value) {
        return value === this.model.password;
    }

    onSubmit() {
        fetch('/api/Account/Register', { method: 'POST', body: this.model })
            .then(response => response.json() as Promise<RegisterViewModel>)
            .then(data => {
                if (data.redirect) {
                    this.$router.push(data.returnUrl);
                } else {
                    this.model.errors = data.errors;
                }
            });
    }
}
